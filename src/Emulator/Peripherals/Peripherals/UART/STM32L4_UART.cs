//
// Copyright (c) 2010-2019 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using System.Collections.Generic;
using Antmicro.Migrant;
using Antmicro.Renode.Core.Structure.Registers;

namespace Antmicro.Renode.Peripherals.UART
{
    [AllowedTranslations(AllowedTranslation.WordToDoubleWord | AllowedTranslation.ByteToDoubleWord)]
    public class STM32L4_UART: BasicDoubleWordPeripheral, IUART
    {
        public STM32L4_UART(Machine machine, uint frequency = 8000000) : base(machine)
        {
            this.frequency = frequency;
        }

        public void WriteChar(byte value)
        {
            if (!usartEnabled.Value && !receiverEnabled.Value)
            {
                this.Log(LogLevel.Warning, "Received a character, but the receiver is not enabled, dropping.");
                return;
            }
            receiveFifo.Enqueue(value);
            readFifoNotEmpty.Value = true;
            Update();
        }

        public override void Reset()
        {
            base.Reset();
            receiveFifo.Clear();
        }

        public uint BaudRate
        {
            get
            {
                var fraction = oversamplingMode.Value == OversamplingMode.By16 ? frequency : 2 * frequency;
                var divisor = oversamplingMode.Value == OversamplingMode.By16 ? baudRateRegister.Value : (baudRateRegister.Value & 0xFFF0 | ((baudRateRegister.Value & 0x0007) << 1));

                return divisor == 0 ? 0 : (uint)(fraction / divisor);
            }
        }

        public Bits StopBits
        {
            get
            {
                switch (stopBits.Value)
                {
                    case StopBitsValues.Half:
                        return Bits.Half;
                    case StopBitsValues.One:
                        return Bits.One;
                    case StopBitsValues.OneAndAHalf:
                        return Bits.OneAndAHalf;
                    case StopBitsValues.Two:
                        return Bits.Two;
                    default:
                        throw new ArgumentException("Invalid stop bits value");
                }
            }
        }

        public Parity ParityBit => parityControlEnabled.Value ?
                                    (paritySelection.Value == ParitySelection.Even ?
                                        Parity.Even :
                                        Parity.Odd) :
                                    Parity.None;

        public GPIO IRQ { get; } = new GPIO();

        public event Action<byte> CharReceived;

        protected override void DefineRegisters()
        {
            Register.Control1.Define(this, 0x00000000, name: "USART_CR1")
                .WithFlag(0, out usartEnabled, name: "UE")
                .WithTaggedFlag("UESM", 1)
                .WithFlag(2, out receiverEnabled, name: "RE")
                .WithFlag(3, out transmitterEnabled, name: "TE")
                .WithTaggedFlag("IDLEIE", 4)
                .WithFlag(5, out receiverNotEmptyInterruptEnabled, name: "RXNEIE")
                .WithFlag(6, out transmissionCompleteInterruptEnabled, name: "TCIE")
                .WithFlag(7, out transmitDataRegisterEmptyInterruptEnabled, name: "TXEIE")
                .WithTaggedFlag("PEIE", 8)
                .WithEnumField(9, 1, out paritySelection, name: "PS")
                .WithFlag(10, out parityControlEnabled, name: "PCE")
                .WithTaggedFlag("WAKE", 11)
                .WithTaggedFlag("M0", 12)
                .WithTaggedFlag("MME", 13)
                .WithTaggedFlag("CMIE", 14)
                .WithEnumField(15, 1, out oversamplingMode, name: "OVER8")
                .WithTag("DEDT", 16, 5)
                .WithTag("DEAT", 21, 5)
                .WithTaggedFlag("RTOIE", 26)
                .WithTaggedFlag("EOBIE", 27)
                .WithTaggedFlag("M1", 28)
                .WithReservedBits(29, 3)
                .WithWriteCallback((_, __) => Update())
            ;
            Register.Control2.Define(this, 0x00000000, name: "USART_CR2")
                .WithReservedBits(0, 4)
                .WithTaggedFlag("ADDM7", 4)
                .WithTaggedFlag("LBDL", 5)
                .WithTaggedFlag("LBDIE", 6)
                .WithReservedBits(7, 1)
                .WithTaggedFlag("LBCL", 8)
                .WithTaggedFlag("CPHA", 9)
                .WithTaggedFlag("CPOL", 10)
                .WithTaggedFlag("CLKEN", 11)
                .WithEnumField(12, 2, out stopBits, name: "STOP")
                .WithTaggedFlag("LINEN", 14)
                .WithTaggedFlag("SWAP", 15)
                .WithTaggedFlag("RXINV", 16)
                .WithTaggedFlag("TXINV", 17)
                .WithTaggedFlag("DATAINV", 18)
                .WithTaggedFlag("MSBFIRST", 19)
                .WithTag("ABRMOD", 21, 2)
                .WithTaggedFlag("RTOEN", 23)
                .WithTag("ADD", 24, 8) // ADD 0:3 and 4:7
            ;
            Register.Control3.Define(this, 0x00000000, name: "USART_CR3")
                .WithTaggedFlag("EIE", 0)
                .WithTaggedFlag("IREN", 1)
                .WithTaggedFlag("IRLP", 2)
                .WithTaggedFlag("HDSEL", 3)
                .WithTaggedFlag("NACK", 4)
                .WithTaggedFlag("SCEN", 5)
                .WithTaggedFlag("DMAR", 6)
                .WithTaggedFlag("DMAT", 7)
                .WithTaggedFlag("RTSE", 8)
                .WithTaggedFlag("CTSE", 9)
                .WithTaggedFlag("CTSIE", 10)
                .WithTaggedFlag("ONEBIT", 11)
                .WithTaggedFlag("OVRDIS", 12)
                .WithTaggedFlag("DDRE", 13)
                .WithTaggedFlag("DEM", 14)
                .WithTaggedFlag("DEP", 15)
                .WithReservedBits(16, 1)
                .WithTaggedFlag("SCARCNT0", 17)
                .WithTaggedFlag("SCARCNT1", 18)
                .WithTaggedFlag("SCARCNT2", 19)
                .WithTaggedFlag("WUS0", 20)
                .WithTaggedFlag("WUS1", 21)
                .WithTaggedFlag("WUFIE", 22)
                .WithTaggedFlag("UCESM", 23)
                .WithTaggedFlag("TCBGTIE", 24)
                .WithReservedBits(25, 7)
            ;
            Register.BaudRate.Define(this, 0x00000000, name: "USART_BRR")
                .WithValueField(0, 15, out baudRateRegister, name: "BRR")
                .WithReservedBits(16, 16)
            ;
            Register.GuardTimeAndPrescaler.Define(this, 0x00000000, name: "USART_GTPR")
                .WithTag("PSC", 0, 8)
                .WithTag("GT", 8, 8)
            ;
            Register.ReceiverTimeout.Define(this, 0x00000000, name: "USART_RTOR")
                .WithTag("RTO", 0, 24)
                .WithTag("BLEN", 24, 8)
            ;
            Register.Request.Define(this, 0x00000000, name: "USART_RQR")
                .WithTaggedFlag("ABRRQ", 0)
                .WithTaggedFlag("SBKRQ", 1)
                .WithTaggedFlag("MMRQ", 2)
                .WithTaggedFlag("RXFRQ", 3)
                .WithTaggedFlag("TXFRQ", 4)
                .WithReservedBits(5, 27)
            ;
            Register.InterruptAndStatus.Define(this, 0x020000C0, "USART_ISR")
                .WithTaggedFlag("PE", 0)
                .WithTaggedFlag("FE", 1)
                .WithTaggedFlag("NF", 2)
                .WithFlag(3, FieldMode.Read, valueProviderCallback: _ => false, name: "ORE") // we assume no receive overruns
                .WithTaggedFlag("IDLE", 4)
                .WithFlag(5, out readFifoNotEmpty, FieldMode.Read | FieldMode.WriteZeroToClear, name: "RXNE") // as these two flags are WZTC, we cannot just calculate their results
                .WithFlag(6, out transmissionComplete, FieldMode.Read | FieldMode.WriteZeroToClear, name: "TC")
                .WithFlag(7, FieldMode.Read, valueProviderCallback: _ => true, name: "TXE") // we always assume "transmit data register empty"
                .WithTaggedFlag("LBDF", 8)
                .WithTaggedFlag("CTSIF", 9)
                .WithTaggedFlag("CTS", 10)
                .WithTaggedFlag("RTOF", 11)
                .WithTaggedFlag("EOBF", 12)
                .WithReservedBits(13, 1)
                .WithTaggedFlag("ABRE", 14)
                .WithTaggedFlag("ABRF", 15)
                .WithTaggedFlag("BUSY", 16)
                .WithTaggedFlag("CMF", 17)
                .WithTaggedFlag("SBKf", 18)
                .WithTaggedFlag("RWU", 19)
                .WithTaggedFlag("WUF", 20)
                .WithTaggedFlag("TEACK", 21)
                .WithTaggedFlag("REACK", 22)
                .WithReservedBits(23, 2)
                .WithTaggedFlag("TCBGT", 25)
                .WithReservedBits(26, 6)
                .WithWriteCallback((_, __) => Update())
            ;
            Register.InterruptFlagClear.Define(this, 0x00000000, "USART_ICR")
                .WithTaggedFlag("PECF", 0)
                .WithTaggedFlag("FECF", 1)
                .WithTaggedFlag("NCF", 2)
                .WithTaggedFlag("ORECF", 3)
                .WithTaggedFlag("IDLECF", 4)
                .WithReservedBits(5, 1)
                .WithTaggedFlag("TCCF", 6)
                .WithTaggedFlag("TCBGTCF", 7)
                .WithTaggedFlag("LBDCF", 8)
                .WithTaggedFlag("CTSCF", 9)
                .WithReservedBits(10, 1)
                .WithTaggedFlag("RTOCF", 11)
                .WithTaggedFlag("EOBCF", 12)
                .WithReservedBits(13, 4)
                .WithTaggedFlag("CMCF", 17)
                .WithReservedBits(18, 2)
                .WithTaggedFlag("WUCF", 20)
                .WithReservedBits(21, 11)
            ;
            Register.ReceiveData.Define(this, name: "USART_RDR")
                .WithValueField(0, 9, FieldMode.Read, valueProviderCallback: _ =>
                    {
                        uint value = 0;
                        if (receiveFifo.Count > 0)
                        {
                            value = receiveFifo.Dequeue();
                        }
                        readFifoNotEmpty.Value = receiveFifo.Count > 0;
                        Update();
                        return value;
                    }, name: "RDR")
                .WithReservedBits(9, 23)
               ;
            Register.TransmitData.Define(this, name: "USART_TDR")
                .WithValueField(0, 9, FieldMode.Read | FieldMode.Write, writeCallback: (_, value) =>
                    {
                        if (!usartEnabled.Value && !transmitterEnabled.Value)
                        {
                            this.Log(LogLevel.Warning, "Trying to transmit a character, but the transmitter is not enabled. dropping.");
                            return;
                        }
                        CharReceived?.Invoke((byte)value);
                        transmissionComplete.Value = true;
                        Update();
                    }, name: "TDR")
                .WithReservedBits(9, 23)
               ;
        }

        private void Update()
        {
            IRQ.Set(
                (receiverNotEmptyInterruptEnabled.Value && readFifoNotEmpty.Value) ||
                (transmitDataRegisterEmptyInterruptEnabled.Value) || // TXE is assumed to be true
                (transmissionCompleteInterruptEnabled.Value && transmissionComplete.Value)
            );
        }

        private readonly uint frequency;

        private IEnumRegisterField<OversamplingMode> oversamplingMode;
        private IEnumRegisterField<StopBitsValues> stopBits;
        private IFlagRegisterField usartEnabled;
        private IFlagRegisterField parityControlEnabled;
        private IEnumRegisterField<ParitySelection> paritySelection;
        private IFlagRegisterField transmissionCompleteInterruptEnabled;
        private IFlagRegisterField transmitDataRegisterEmptyInterruptEnabled;
        private IFlagRegisterField receiverNotEmptyInterruptEnabled;
        private IFlagRegisterField receiverEnabled;
        private IFlagRegisterField transmitterEnabled;
        private IFlagRegisterField readFifoNotEmpty;
        private IFlagRegisterField transmissionComplete;
        private IValueRegisterField baudRateRegister;

        private readonly Queue<byte> receiveFifo = new Queue<byte>();

        private enum OversamplingMode
        {
            By16 = 0,
            By8 = 1
        }

        private enum StopBitsValues
        {
            One = 0,
            Half = 1,
            Two = 2,
            OneAndAHalf = 3
        }

        private enum ParitySelection
        {
            Even = 0,
            Odd = 1
        }

        private enum Register : long
        {
            Control1 = 0x00,
            Control2 = 0x04,
            Control3 = 0x08,
            BaudRate = 0x0C,
            GuardTimeAndPrescaler = 0x10,
            ReceiverTimeout = 0x14,
            Request = 0x18,
            InterruptAndStatus = 0x1C,
            InterruptFlagClear = 0x20,
            ReceiveData = 0x24,
            TransmitData = 0x28
        }
    }
}
