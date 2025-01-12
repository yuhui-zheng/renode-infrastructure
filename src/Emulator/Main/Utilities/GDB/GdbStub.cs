//
// Copyright (c) 2010-2018 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Utilities.GDB
{
    public class GdbStub : IDisposable, IExternal
    {
        public GdbStub(Machine machine, IEnumerable<ICpuSupportingGdb> cpus, int port, bool autostartEmulation)
        {
            this.cpus = cpus;
            Port = port;

            LogsEnabled = true;

            pcktBuilder = new PacketBuilder();
            commandsManager = new CommandsManager(machine, cpus);
            commandsManager.ShouldAutoStart = autostartEmulation;
            TypeManager.Instance.AutoLoadedType += commandsManager.Register;

            terminal = new SocketServerProvider();
            terminal.DataReceived += OnByteWritten;
            terminal.ConnectionAccepted += delegate
            {
                commandsManager.CanAttachCPU = false;
                foreach(var cpu in cpus)
                {
                    cpu.Halted += OnHalted;
                    cpu.ExecutionMode = ExecutionMode.SingleStep;
                    cpu.DebuggerConnected = true;
                }
            };
            terminal.ConnectionClosed += delegate
            {
                foreach(var cpu in cpus)
                {
                    cpu.Halted -= OnHalted;
                    cpu.ExecutionMode = ExecutionMode.Continuous;
                    cpu.DebuggerConnected = false;
                }
                commandsManager.CanAttachCPU = true;
            };
            terminal.Start(port);
            commHandler = new CommunicationHandler(this, commandsManager);

            LogsEnabled = false;
        }

        public void AttachCPU(ICpuSupportingGdb cpu)
        {
            commandsManager.AttachCPU(cpu);
        }

        public bool IsCPUAttached(ICpuSupportingGdb cpu)
        {
            return commandsManager.IsCPUAttached(cpu);
        }

        public void Dispose()
        {
            foreach(var cpu in cpus)
            {
                cpu.Halted -= OnHalted;
            }
            terminal.Dispose();
        }

        public int Port { get; private set; }

        public bool LogsEnabled { get; set; }

        private void OnHalted(HaltArguments args)
        {
            using(var ctx = commHandler.OpenContext())
            {
                // GDB counts threads starting from `1`, while Renode counts them from `0` - hence the incrementation
                var cpuId = args.CpuId + 1;
                switch(args.Reason)
                {
                case HaltReason.Breakpoint:
                    switch(args.BreakpointType)
                    {
                    case BreakpointType.AccessWatchpoint:
                    case BreakpointType.WriteWatchpoint:
                    case BreakpointType.ReadWatchpoint:
                    case BreakpointType.HardwareBreakpoint:
                    case BreakpointType.MemoryBreakpoint:
                        if(commandsManager.Machine.SystemBus.IsMultiCore)
                        {
                            commandsManager.SelectCpuForDebugging(cpuId);
                            ctx.Send(new Packet(PacketData.StopReply(args.BreakpointType.Value, cpuId, args.Address)));
                        }
                        else
                        {
                            ctx.Send(new Packet(PacketData.StopReply(args.BreakpointType.Value, args.Address)));
                        }
                        break;
                    }
                    return;
                case HaltReason.Step:
                case HaltReason.Pause:
                    if(commandsManager.Machine.SystemBus.IsMultiCore)
                    {
                        commandsManager.SelectCpuForDebugging(cpuId);
                        ctx.Send(new Packet(PacketData.StopReply(TrapSignal, cpuId)));
                    }
                    else
                    {
                        ctx.Send(new Packet(PacketData.StopReply(TrapSignal)));
                    }
                    return;
                case HaltReason.Abort:
                    ctx.Send(new Packet(PacketData.AbortReply(AbortSignal)));
                    return;
                default:
                    throw new ArgumentException("Unexpected halt reason");
                }
            }
        }

        private void OnByteWritten(int b)
        {
            if(b == -1)
            {
                return;
            }
            var result = pcktBuilder.AppendByte((byte)b);
            if(result == null)
            {
                return;
            }

            if(result.Interrupt)
            {
                if(LogsEnabled)
                {
                    commandsManager.Cpu.Log(LogLevel.Noisy, "GDB CTRL-C occured - pausing CPU");
                }
                // we need to pause CPU in order to escape infinite loops
                commandsManager.Cpu.Pause();
                commandsManager.Cpu.ExecutionMode = ExecutionMode.SingleStep;
                commandsManager.Cpu.Resume();
                return;
            }

            using(var ctx = commHandler.OpenContext())
            {
                if(result.CorruptedPacket)
                {
                    if(LogsEnabled)
                    {
                        commandsManager.Cpu.Log(LogLevel.Warning, "Corrupted GDB packet received: {0}", result.Packet.Data.DataAsString);
                    }
                    // send NACK
                    ctx.Send((byte)'-');
                    return;
                }

                if(LogsEnabled)
                {
                    commandsManager.Cpu.Log(LogLevel.Debug, "GDB packet received: {0}", result.Packet.Data.DataAsString);
                }
                // send ACK
                ctx.Send((byte)'+');

                Command command;
                if(!commandsManager.TryGetCommand(result.Packet, out command))
                {
                    if(LogsEnabled)
                    {
                        commandsManager.Cpu.Log(LogLevel.Warning, "Unsupported GDB command: {0}", result.Packet.Data.DataAsString);
                    }
                    ctx.Send(new Packet(PacketData.Empty));
                }
                else
                {
                    var packetData = Command.Execute(command, result.Packet);
                    // null means that we will respond later with Stop Reply Response
                    if(packetData != null)
                    {
                        ctx.Send(new Packet(packetData));
                    }
                }
            }
        }

        private readonly PacketBuilder pcktBuilder;
        private readonly IEnumerable<ICpuSupportingGdb> cpus;
        private readonly SocketServerProvider terminal;
        private readonly CommandsManager commandsManager;
        private readonly CommunicationHandler commHandler;

        private const int TrapSignal = 5;
        private const int AbortSignal = 6;

        private class CommunicationHandler
        {
            public CommunicationHandler(GdbStub stub, CommandsManager manager)
            {
                this.stub = stub;
                this.manager = manager;
                queue = new Queue<byte>();
                internalLock = new object();
            }

            public Context OpenContext()
            {
                lock(internalLock)
                {
                    counter++;
                    if(counter > 1)
                    {
                        if(stub.LogsEnabled)
                        {
                            manager.Cpu.Log(LogLevel.Debug, "Gdb stub: entering nested communication context. All bytes will be queued.");
                        }
                    }
                    return new Context(this, counter > 1);
                }
            }

            public void SendByteDirect(byte b)
            {
                stub.terminal.SendByte(b);
            }

            private void SendAllBufferedData()
            {
                foreach(var b in queue)
                {
                    stub.terminal.SendByte(b);
                }
                queue.Clear();
            }

            private void ContextClosed(IEnumerable<byte> buffer)
            {
                lock(internalLock)
                {
                    if(buffer != null)
                    {
                        foreach(var b in buffer)
                        {
                            queue.Enqueue(b);
                        }
                    }

                    counter--;
                    if(counter == 0 && queue.Count > 0)
                    {
                        if(stub.LogsEnabled)
                        {
                            manager.Cpu.Log(LogLevel.Debug, "Gdb stub: leaving nested communication context. Sending {0} queued bytes.", queue.Count);
                        }
                        SendAllBufferedData();
                    }
                }
            }

            private readonly GdbStub stub;
            private readonly CommandsManager manager;
            private readonly Queue<byte> queue;
            private readonly object internalLock;
            private int counter;

            public class Context : IDisposable
            {
                public Context(CommunicationHandler commHandler, bool useBuffering)
                {
                    this.commHandler = commHandler;
                    if(useBuffering)
                    {
                        buffer = new Queue<byte>();
                    }
                }

                public void Dispose()
                {
                    commHandler.ContextClosed(buffer);
                }

                public void Send(Packet packet)
                {
                    if(commHandler.stub.LogsEnabled)
                    {
                        commHandler.manager.Cpu.Log(LogLevel.Debug, "Sending response to GDB: {0}", packet.Data.DataAsString);
                    }
                    foreach(var b in packet.GetCompletePacket())
                    {
                        Send(b);
                    }
                }

                public void Send(byte b)
                {
                    if(buffer == null)
                    {
                        commHandler.SendByteDirect(b);
                    }
                    else
                    {
                        buffer.Enqueue(b);
                    }
                }

                private readonly CommunicationHandler commHandler;
                private readonly Queue<byte> buffer;
            }
        }
    }
}

