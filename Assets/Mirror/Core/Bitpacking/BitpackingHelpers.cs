using System;

namespace Mirror.Core
{
    public static class BitpackingHelpers 
    {
        public struct IntegerFormatInfo
        {
            public bool Signed;
            public int Bits;
        }

        public struct DecimalFormatInfo
        {
            public bool Signed;
            public ushort ExponentBits;
            public short BiasExponent;
            public ushort MantissaBits;
        }

        public static long FindNextPowerOf2Exponent(double x)
        {
            if (x <= 0) throw new System.Exception("X must be positive");
            return (long)System.Math.Ceiling(Log2(x));
        }
        public static long FindPreviousPowerOf2Exponent(double x)
        {
            if (x <= 0) throw new System.Exception("X must be positive");
            return (long)System.Math.Floor(Log2(x));
        }

        public static double Log2(double x)
        {
            return System.Math.Log(x) / System.Math.Log(2.0f);
        }



        // Taking in basic format info instead of struct to make the ILcode generation a bit simpler.
        public static void WriteIntegerHelper(NetworkWriter writer, byte value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, value, typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelper(NetworkWriter writer, sbyte value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, unchecked((ulong)(long)value), typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelper(NetworkWriter writer, ushort value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, value, typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelper(NetworkWriter writer, short value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, unchecked((ulong)(long)value), typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelper(NetworkWriter writer, uint value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, value, typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelper(NetworkWriter writer, int value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, unchecked((ulong)(long)value), typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelper(NetworkWriter writer, ulong value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, value, typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelper(NetworkWriter writer, long value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, unchecked((ulong)value), typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);
        private static void WriteIntegerHelperCore(NetworkWriter writer, ulong value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
        {
            int bitsToWrite = formatBits;
            int remainderBits = bitsToWrite % 8;
            if (formatSigned)
            {
                WritePartialByte(writer, 1, (byte)(value >> (typeBits - 1)), ref bitOffset, ref currentByte);
            }
            if (remainderBits != 0)
            {
                WritePartialByte(writer, remainderBits, (byte)(value >> (bitsToWrite - remainderBits)), ref bitOffset, ref currentByte);
                bitsToWrite -= remainderBits;
            }
            while (bitsToWrite > 0)
            {
                WritePartialByte(writer, 8, (byte)(value >> (bitsToWrite - 8)), ref bitOffset, ref currentByte);
                bitsToWrite -= 8;
            }
        }

        private static ulong ReadIntegerHelperCore(NetworkReader reader, bool formatSigned, int formatBits, int typeBits, ref int bitOffset, ref byte currentByte)
        {
            int bitsToRead = formatBits;

            // TODO. using readPartialByte I think. 
            ulong result;
            return result;
        }


        public static void WriteDoubleHelper(NetworkWriter writer, double value, DecimalFormatInfo format, ref int bitOffset, ref byte currentByte)
        {
            // 32 - bit IEEE 754
            // 1 sign bit, 8 exponent bits, 23 mantissa bits

        }
        public static void WriteFloatHelper(NetworkWriter writer, float value, DecimalFormatInfo format, ref int bitOffset, ref byte currentByte)
        {
            // 64-bit IEEE 754
            // 1 sign bit, 11 exponent bits, 52 mantissa bits

        }

        // writes right-most (least significant bits) BITS from VALUE into BYTES
        public static void WritePartialByte(NetworkWriter writer, int bits, byte value, ref int bitOffset, ref byte currentByte)
        {
            if (bits <= 0 || bits > 8)
                throw new ArgumentException("bits must be between 1 and 8");

            // keep the right-most bits
            value = (byte)(value << (8 - bits));

            int bitPos = bitOffset % 8;

            int freeBitsInCurrentByte = 8 - bitPos;

            // Shift the bits to the correct position within the byte
            byte firstPart = (byte)(value >> bitPos);
            currentByte |= firstPart;

            if (bits >= freeBitsInCurrentByte)
            {
                // flush the current byte
                writer.Write(currentByte);
                currentByte = 0;

                // Write remainder to next byte (least significant bits)
                byte secondPart = (byte)(value << freeBitsInCurrentByte);
                currentByte |= secondPart;
            }

            bitOffset += bits;
        }

        public static byte ReadPartialByte(NetworkReader reader, int bits, ref int bitOffset, ref byte currentByte)
        {
            if (bits <= 0 || bits > 8)
                throw new ArgumentException("bits must be between 1 and 8");

            int bitPos = bitOffset % 8;

            // If we're at the start of a new byte, read it
            if (bitPos == 0)
            {
                currentByte = reader.ReadByte();
            }

            int freeBitsInCurrentByte = 8 - bitPos;
            byte result;

            if (bits <= freeBitsInCurrentByte)
            {
                // All bits are in current byte
                result = (byte)((currentByte >> (freeBitsInCurrentByte - bits)) & ((1 << bits) - 1));
            }
            else
            {
                // Need bits from current byte and next byte
                byte firstPart = (byte)((currentByte & ((1 << freeBitsInCurrentByte) - 1)) << (bits - freeBitsInCurrentByte));

                currentByte = reader.ReadByte();
                int remainingBits = bits - freeBitsInCurrentByte;
                byte secondPart = (byte)((currentByte >> (8 - remainingBits)) & ((1 << remainingBits) - 1));

                result = (byte)(firstPart | secondPart);
            }

            bitOffset += bits;
            return result;
        }

    }

}
