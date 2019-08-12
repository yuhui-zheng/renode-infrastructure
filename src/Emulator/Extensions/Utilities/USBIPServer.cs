//
// Copyright (c) 2010-2019 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.USB;
using Antmicro.Renode.Debugging;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Utilities.Packets;

namespace Antmicro.Renode.Extensions
{
    public static class USBIPServerExtensions
    {
        public static void CreateUSBIPServer(this Emulation emulation, int port = 3240, string address = "127.0.0.1")
        {
#if PLATFORM_LINUX
            var server = new USBIPServer(address, port);
            server.Run();

            emulation.HostMachine.AddHostMachineElement(server, "usb");
#else
            throw new RecoverableException("USB/IP server is currently available on Linux only");
#endif
        }
    }

    public class USBIPServer : SimpleContainerBase<IUSBDevice>, IHostMachineElement
    {
        public USBIPServer(string address, int port)
        {
            this.port = port;

            server = new SocketServerProvider(false);
            server.DataReceived += HandleIncomingData;

            buffer = new List<byte>(Packet.CalculateLength<USBIPURBRequest>());
        }

        public void Run()
        {
            server.Start(port);
        }

        private void SendResponse(IEnumerable<byte> bytes)
        {
            server.Send(bytes);

#if DEBUG_PACKETS
            // dumping packets may severely lower performance
            this.Log(LogLevel.Noisy, "Count {0}: {1}", bytes.Count(), Misc.PrettyPrintCollection(bytes, x => "0x{0:X}".FormatWith(x)));
#endif
        }

        private void HandleIncomingData(int b)
        {
            this.Log(LogLevel.Noisy, "Incoming byte: 0x{0:X}; state = {1}; buffer size = {2}", b, state, buffer.Count);

            if(b < 0)
            {
                buffer.Clear();
                state = State.WaitForCommand;
                return;
            }

            buffer.Add((byte)b);

            switch(state)
            {
                case State.WaitForCommand:
                {
                    DebugHelper.Assert(buffer.Count <= Packet.CalculateLength<USBIPHeader>());
                    if(buffer.Count == Packet.CalculateLength<USBIPHeader>())
                    {
                        var header = Packet.Decode<USBIPHeader>(buffer);
                        this.Log(LogLevel.Debug, "Received USB/IP header: {0}", header.ToString());

                        switch(header.Command)
                        {
                            case USBIPCommand.ListDevices:
                            {
                                SendResponse(HandleListDevicesCommand());

                                buffer.Clear();
                                state = State.WaitForCommand;
                                break;
                            }

                            case USBIPCommand.AttachDevice:
                            {
                                state = State.WaitForBusId;
                                break;
                            }

                            default:
                            {
                                this.Log(LogLevel.Warning, "Unexpected packet command: 0x{0:X}. Dropping the rest of the packet", header.Command);
                                state = State.DropCommand;
                                break;
                            }
                        }
                    }
                    break;
                }

                case State.WaitForBusId:
                {
                    DebugHelper.Assert(buffer.Count <= Packet.CalculateLength<USBIPAttachDeviceCommandDescriptor>());
                    if(buffer.Count == Packet.CalculateLength<USBIPAttachDeviceCommandDescriptor>())
                    {
                        var busIdPart = buffer.Skip(Packet.CalculateLength<USBIPHeader>()).Take(Packet.CalculateLength<USBIPAttachDeviceCommandDescriptor>() - Packet.CalculateLength<USBIPHeader>()).ToArray();
                        var busId = System.Text.Encoding.ASCII.GetString(busIdPart);

                        buffer.Clear();
                        state = TryHandleDeviceAttachCommand(busId, out var response)
                            ? State.WaitForHeader
                            : State.WaitForCommand;

                        SendResponse(response);
                    }
                    break;
                }

                case State.WaitForHeader:
                {
                    DebugHelper.Assert(buffer.Count <= Packet.CalculateLength<USBIPURBHeader>());
                    if(buffer.Count == Packet.CalculateLength<USBIPURBHeader>())
                    {
                        var urbHeader = Packet.Decode<USBIPURBHeader>(buffer);
                        switch(urbHeader.Command)
                        {
                            case URBCommand.URBRequest:
                                state = State.HandleSubmitURBCommand;
                                break;
                            case URBCommand.Unlink:
                                state = State.HandleUnlinkCommand;
                                break;
                            default:
                                this.Log(LogLevel.Error, "Unexpected URB command: 0x{0:X}. Droping the rest of the packet", urbHeader.Command);
                                state = State.DropCommand;
                                break;
                        }
                    }
                    break;
                }

                case State.DropCommand:
                {
                    DebugHelper.Assert(buffer.Count <= Packet.CalculateLength<USBIPURBRequest>());
                    if(buffer.Count == Packet.CalculateLength<USBIPURBRequest>())
                    {
                        buffer.Clear();
                        state = State.WaitForHeader;
                    }
                    break;
                }

                case State.HandleSubmitURBCommand:
                {
                    DebugHelper.Assert(buffer.Count <= Packet.CalculateLength<USBIPURBRequest>());
                    if(buffer.Count == Packet.CalculateLength<USBIPURBRequest>())
                    {
                        var packet = Packet.Decode<USBIPURBRequest>(buffer);
                        this.Log(LogLevel.Debug, "Received URB request: {0}", packet.ToString());

                        if(packet.BusId != ExportedBusId)
                        {
                            this.Log(LogLevel.Warning, "URB command directed to a non-existing bus 0x{0:X}", packet.BusId);
                        }
                        else if(!TryGetByAddress((int)packet.DeviceId, out var device))
                        {
                            this.Log(LogLevel.Warning, "URB command directed to a non-existing device 0x{0:X}", packet.DeviceId);
                        }
                        else if(packet.EndpointNumber == 0)
                        {
                            var setupPacket = Packet.Decode<SetupPacket>(buffer, Packet.CalculateOffset<USBIPURBRequest>(nameof(USBIPURBRequest.Setup)));
                            var setupPacketResponse = device.USBCore.HandleSetupPacket(setupPacket);
                            SendResponse(GenerateURBReply(packet, setupPacketResponse));
                        }
                        else
                        {
                            var ep = device.USBCore.GetEndpoint((int)packet.EndpointNumber);
                            if(ep == null)
                            {
                                this.Log(LogLevel.Warning, "URB command directed to a non-existing endpoint 0x{0:X}", packet.EndpointNumber);
                            }
                            else if(ep.Direction == Direction.DeviceToHost)
                            {
                                var response = ep.Read((int)packet.TransferBufferLength);
                                SendResponse(GenerateURBReply(packet, response));
                            }
                            else
                            {
                                writePacket = packet;
                                writeEndpoint = ep;
                                state = State.WaitForData;
                                break;
                            }
                        }

                        state = State.WaitForHeader;
                        buffer.Clear();
                    }
                    break;
                }

                case State.WaitForData:
                {
                    DebugHelper.Assert(writeEndpoint != null);
                    var dataOffset = Packet.CalculateLength<USBIPURBRequest>();
                    if(buffer.Count == dataOffset + writePacket.TransferBufferLength)
                    {
                        var data = new byte[writePacket.TransferBufferLength];
                        for(int i = 0; i < data.Length; i++)
                        {
                            data[i] = buffer[i + dataOffset];
                        }
                        writeEndpoint.WriteData(data);

                        SendResponse(GenerateURBReply(writePacket));

                        writeEndpoint = null;
                        state = State.WaitForHeader;
                        buffer.Clear();
                    }
                    break;
                }

                case State.HandleUnlinkCommand:
                {
                    DebugHelper.Assert(buffer.Count <= Packet.CalculateLength<USBIPURBRequest>());
                    if(buffer.Count == Packet.CalculateLength<USBIPURBRequest>())
                    {
                        // this command is practically ignored
                        buffer.Clear();
                        state = State.WaitForHeader;
                    }
                    break;
                }

                default:
                    throw new ArgumentException(string.Format("Unexpected state: {0}", state));
            }
        }

        private IEnumerable<byte> GenerateURBReply(USBIPURBRequest req, IEnumerable<byte> data = null, uint status = 0)
        {
            var header = new USBIPURBReply
            {
                Command = URBCommand.URBReply,
                SequenceNumber = req.SequenceNumber,
                DeviceId = req.DeviceId,
                Direction = req.Direction,
                EndpointNumber = req.EndpointNumber,
                Status = status,
                ErrorCount = status,
                ActualLength = (data == null)
                    ? req.TransferBufferLength
                    : (uint)data.Count()
            };

            var result = Packet.Encode(header).AsEnumerable();
            if(data != null)
            {
                result = result.Concat(data);
            }

            return result;
        }

        private IEnumerable<byte> GenerateDeviceDescirptor(IUSBDevice device, uint deviceNumber, bool includeInterfaces)
        {
            var devDescriptor = new USBIPDeviceDescriptor
            {
                Path = new byte[256],
                BusId = new byte[32],

                BusNumber = ExportedBusId,
                DeviceNumber = deviceNumber,
                Speed = (int) USBSpeed.High, // this is hardcoded, but I don't know if that's good

                IdVendor = (ushort)device.USBCore.VendorId,
                IdProduct = (ushort)device.USBCore.ProductId,

                DeviceClass = (byte)device.USBCore.Class,
                DeviceSubClass = device.USBCore.SubClass,
                DeviceProtocol = device.USBCore.Protocol,

                NumberOfConfigurations = (byte)device.USBCore.Configurations.Count,
                NumberOfInterfaces = (byte)device.USBCore.Configurations.Single().Interfaces.Count
            };

            SetText(devDescriptor.BusId, "{0}-{1}", ExportedBusId, deviceNumber);
            SetText(devDescriptor.Path, "/renode/virtual/{0}-{1}", ExportedBusId, deviceNumber);

            var result = Packet.Encode(devDescriptor).AsEnumerable();

            if(includeInterfaces)
            {
                foreach(var configuration in device.USBCore.Configurations)
                {
                    foreach(var iface in configuration.Interfaces)
                    {
                        var intDescriptor = new USBIPInterfaceDescriptor
                        {
                            InterfaceClass = (byte)iface.Class,
                            InterfaceSubClass = iface.SubClass,
                            InterfaceProtocol = iface.Protocol
                        };

                        result = result.Concat(Packet.Encode(intDescriptor));
                    }
                }
            }

            return result;

            void SetText(byte[] destination, string format, params object[] param)
            {
                var textBuffer = System.Text.Encoding.ASCII.GetBytes(string.Format(format, param));
                Array.Copy(textBuffer, destination, textBuffer.Length);
            }
        }

        private IEnumerable<byte> HandleListDevicesCommand()
        {
            var header = new USBIPHeader
            {
                Version = ProtocolVersion,
                Command = USBIPCommand.ListDevicesReply,
            };

            var regCount = new USBIPDeviceListCount
            {
                NumberOfExportedDevices = (uint)ChildCollection.Count
            };

            var result = Packet.Encode(header).Concat(Packet.Encode(regCount));

            foreach(var child in ChildCollection)
            {
                result = result.Concat(GenerateDeviceDescirptor(child.Value, (uint)child.Key, true));
            }

            return result;
        }

        private bool TryHandleDeviceAttachCommand(string deviceIdString, out IEnumerable<byte> response)
        {
            var success = true;
            var deviceId = 0;
            IUSBDevice device = null;

            var m = Regex.Match(deviceIdString, "([0-9]+)-([0-9]+)");
            if(m == null)
            {
                this.Log(LogLevel.Warning, "Unexpected device: {0}", deviceIdString);
                success = false;
            }
            else
            {
                var busId = int.Parse(m.Groups[1].Value);
                deviceId = int.Parse(m.Groups[2].Value);

                if(busId != ExportedBusId)
                {
                    this.Log(LogLevel.Warning, "Unexpected bus id: {0}", busId);
                    success = false;
                }
                else if(!TryGetByAddress(deviceId, out device))
                {
                    this.Log(LogLevel.Warning, "Unexpected device id: {0}", busId);
                    success = false;
                }
            }

            var header = new USBIPHeader
            {
                Version = ProtocolVersion,
                Command = USBIPCommand.AttachDeviceReply,
                Status = success ? 0 : 1u
            };

            response = Packet.Encode(header).AsEnumerable();
            if(success)
            {
                response = response.Concat(GenerateDeviceDescirptor(device, (uint)deviceId, false));
            }

            return success;
        }

        private State state;
        private USBEndpoint writeEndpoint;
        private USBIPURBRequest writePacket;

        private readonly int port;
        private readonly List<byte> buffer;
        private readonly SocketServerProvider server;

        private const uint ExportedBusId = 1;
        private const ushort ProtocolVersion = 0x0111;

        private enum USBSpeed
        {
            Low = 0,
            Full = 1,
            High = 2,
            Super = 3
        }

        private enum State
        {
            WaitForCommand,
            WaitForBusId,
            WaitForHeader,
            HandleUnlinkCommand,
            HandleSubmitURBCommand,
            DropCommand,
            WaitForData
        }

// 649:  Field '...' is never assigned to, and will always have its default value null
#pragma warning disable 649
        private enum USBIPCommand: ushort
        {
            ListDevices = 0x8005,
            ListDevicesReply = 0x5,
            AttachDevice = 0x8003,
            AttachDeviceReply = 0x3,
        }

        private enum URBCommand: uint
        {
            URBRequest = 0x1,
            Unlink = 0x2,
            URBReply = 0x3,
            UnlinkReply = 0x4,
        }

        private enum DirectionEnum : uint
        {
            Out = 0x0,
            In = 0x1
        }

        private struct USBIPHeader
        {
            [PacketField]
            public ushort Version;
            [PacketField]
            public USBIPCommand Command;
            [PacketField]
            public uint Status;

            public override string ToString()
            {
                return $"Version = 0x{Version:X}, Command = 0x{Command:X}, Status = 0x{Status:X}";
            }
        }

        private struct USBIPAttachDeviceCommandDescriptor
        {
            [PacketField]
            public ushort Version;
            [PacketField]
            public USBIPCommand Command;
            [PacketField]
            public uint Status;
            [PacketField, Width(32)]
            public byte[] BusId;
        }

        private struct USBIPURBHeader
        {
            [PacketField]
            public URBCommand Command;
        }

        private struct USBIPURBRequest
        {
            [PacketField]
            public URBCommand Command;
            [PacketField]
            public uint SequenceNumber;
            [PacketField]
            public ushort BusId; // in the documentation those two fields are called 'DeviceId'
            [PacketField]
            public ushort DeviceId;
            [PacketField]
            public DirectionEnum Direction;
            [PacketField]
            public uint EndpointNumber;
            [PacketField]
            public uint TransferFlags;
            [PacketField]
            public uint TransferBufferLength;
            [PacketField]
            public uint StartFrame;
            [PacketField]
            public uint NumberOfPackets;
            [PacketField]
            public uint Interval;
            [PacketField]
            public ulong Setup;

            public override string ToString()
            {
                return $"Command = {Command}, SequenceNumber = 0x{SequenceNumber:X}, DeviceId = 0x{DeviceId:X}, Direction = {Direction}, EndpointNumber = 0x{EndpointNumber:X}, TransferFlags = 0x{TransferFlags:X}, TransferBufferLength = 0x{TransferBufferLength:X}, StartFrame = 0x{StartFrame:X}, NumberOfPackets = 0x{NumberOfPackets:X}, Interval = 0x{Interval:X}, Setup = 0x{Setup:X}";
            }
        }

        private struct USBIPURBReply
        {
            [PacketField]
            public URBCommand Command;
            [PacketField]
            public uint SequenceNumber;
            [PacketField]
            public uint DeviceId;
            [PacketField]
            public DirectionEnum Direction;
            [PacketField]
            public uint EndpointNumber;
            [PacketField]
            public uint Status;
            [PacketField]
            public uint ActualLength;
            [PacketField]
            public uint StartFrame;
            [PacketField]
            public uint NumberOfPackets;
            [PacketField]
            public uint ErrorCount;
            [PacketField]
            public ulong Setup;

            public override string ToString()
            {
                return $"Command = {Command}, SequenceNumber = 0x{SequenceNumber:X}, DeviceId = 0x{DeviceId:X}, Direction = {Direction}, EndpointNumber = 0x{EndpointNumber:X}, Status = 0x{Status:X}, ActualLength = 0x{ActualLength:X}, StartFrame = 0x{StartFrame:X}, NumberOfPackets = 0x{NumberOfPackets:X}, ErrorCount = 0x{ErrorCount:X}, Setup = 0x{Setup:X}";
            }
        }

        private struct USBIPDeviceListCount
        {
            [PacketField]
            public uint NumberOfExportedDevices;
        }

        private struct USBIPDeviceDescriptor
        {
            [PacketField, Width(256)]
            public byte[] Path;
            [PacketField, Width(32)]
            public byte[] BusId;
            [PacketField]
            public uint BusNumber;
            [PacketField]
            public uint DeviceNumber;
            [PacketField]
            public uint Speed;
            [PacketField]
            public ushort IdVendor;
            [PacketField]
            public ushort IdProduct;
            [PacketField]
            public ushort BcdDevice;
            [PacketField]
            public byte DeviceClass;
            [PacketField]
            public byte DeviceSubClass;
            [PacketField]
            public byte DeviceProtocol;
            [PacketField]
            public byte ConfigurationValue;
            [PacketField]
            public byte NumberOfConfigurations;
            [PacketField]
            public byte NumberOfInterfaces;
        }

        private struct USBIPInterfaceDescriptor
        {
            [PacketField]
            public byte InterfaceClass;
            [PacketField]
            public byte InterfaceSubClass;
            [PacketField]
            public byte InterfaceProtocol;
            [PacketField]
            public byte Padding;
        }
#pragma warning restore 649
    }
}
