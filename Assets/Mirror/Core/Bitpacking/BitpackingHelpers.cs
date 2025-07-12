using System;
using UnityEngine;

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
            public int ExponentBits;
            public int BiasExponent; // where: Bias = (2 ^ (2^BiasExponent - 1))
            public int MantissaBits;
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


        public static void WriteIntegerHelperByte(NetworkWriter writer, byte value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
    => WriteIntegerHelperCore(writer, value, typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelperSByte(NetworkWriter writer, sbyte value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, unchecked((ulong)(long)value), typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelperUShort(NetworkWriter writer, ushort value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, value, typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelperShort(NetworkWriter writer, short value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, unchecked((ulong)(long)value), typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelperUInt(NetworkWriter writer, uint value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, value, typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelperInt(NetworkWriter writer, int value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, unchecked((ulong)(long)value), typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelperULong(NetworkWriter writer, ulong value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
            => WriteIntegerHelperCore(writer, value, typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);

        public static void WriteIntegerHelperLong(NetworkWriter writer, long value, int typeBits, bool formatSigned, int formatBits, ref int bitOffset, ref byte currentByte)
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

        public static byte ReadIntegerHelperByte(NetworkReader reader, bool formatSigned, int formatBits, int typeBits, ref int bitOffset, ref byte currentByte)
    => (byte)ReadIntegerHelperCore(reader, formatSigned, formatBits, typeBits, ref bitOffset, ref currentByte);

        public static sbyte ReadIntegerHelperSByte(NetworkReader reader, bool formatSigned, int formatBits, int typeBits, ref int bitOffset, ref byte currentByte)
            => unchecked((sbyte)(long)ReadIntegerHelperCore(reader, formatSigned, formatBits, typeBits, ref bitOffset, ref currentByte));

        public static ushort ReadIntegerHelperUShort(NetworkReader reader, bool formatSigned, int formatBits, int typeBits, ref int bitOffset, ref byte currentByte)
            => (ushort)ReadIntegerHelperCore(reader, formatSigned, formatBits, typeBits, ref bitOffset, ref currentByte);

        public static short ReadIntegerHelperShort(NetworkReader reader, bool formatSigned, int formatBits, int typeBits, ref int bitOffset, ref byte currentByte)
            => unchecked((short)(long)ReadIntegerHelperCore(reader, formatSigned, formatBits, typeBits, ref bitOffset, ref currentByte));

        public static uint ReadIntegerHelperUInt(NetworkReader reader, bool formatSigned, int formatBits, int typeBits, ref int bitOffset, ref byte currentByte)
            => (uint)ReadIntegerHelperCore(reader, formatSigned, formatBits, typeBits, ref bitOffset, ref currentByte);

        public static int ReadIntegerHelperInt(NetworkReader reader, bool formatSigned, int formatBits, int typeBits, ref int bitOffset, ref byte currentByte)
            => unchecked((int)(long)ReadIntegerHelperCore(reader, formatSigned, formatBits, typeBits, ref bitOffset, ref currentByte));

        public static ulong ReadIntegerHelperULong(NetworkReader reader, bool formatSigned, int formatBits, int typeBits, ref int bitOffset, ref byte currentByte)
            => ReadIntegerHelperCore(reader, formatSigned, formatBits, typeBits, ref bitOffset, ref currentByte);

        public static long ReadIntegerHelperLong(NetworkReader reader, bool formatSigned, int formatBits, int typeBits, ref int bitOffset, ref byte currentByte)
            => unchecked((long)ReadIntegerHelperCore(reader, formatSigned, formatBits, typeBits, ref bitOffset, ref currentByte));
        private static ulong ReadIntegerHelperCore(NetworkReader reader, bool formatSigned, int formatBits, int typeBits, ref int bitOffset, ref byte currentByte)
        {
            ulong result = 0;
            int bitsToRead = formatBits;
            int remainderBits = bitsToRead % 8;

            if (formatSigned)
            {
                result = ReadPartialByte(reader, 1, ref bitOffset, ref currentByte);
                result = result << (typeBits - bitsToRead);
            }
            if(remainderBits != 0)
            {
                result = result | ReadPartialByte(reader, remainderBits, ref bitOffset, ref currentByte);
                bitsToRead -= remainderBits;
            }
            while(bitsToRead > 0)
            {
                result = result << 8;
                result = result | ReadPartialByte(reader, 8, ref bitOffset, ref currentByte);
                bitsToRead -= 8;
            }

            return result;
        }


        public static void WriteFloatHelper(NetworkWriter writer, float value, bool formatSigned, int exponentBits, int biasExponent, int mantissaBits, ref int bitOffset, ref byte currentByte)
        {
            // 32 - bit IEEE 754
            // 1 sign bit, 8 exponent bits, 23 mantissa bits, 127 bias (2^7 - 1)
            int valuebits = BitConverter.SingleToInt32Bits(value);
            // signbit
            if (formatSigned)
            {
                WritePartialByte(writer, 1, (byte)(valuebits >> 31), ref bitOffset, ref currentByte);
            }

            // exponent
            // the generated formats use custom bias by doing that we can lower the lower-end of our floating point precision, down to the user-specified precision.
            int oldExponent = (valuebits >> 23) & 0b011111111;
            int newExponent = oldExponent - 127 + (int)Math.Pow(2, biasExponent) - 1;
            WritePartialByte(writer, exponentBits, (byte)(newExponent), ref bitOffset, ref currentByte);

            int mantissaBitsToWrite = mantissaBits;
            int mantissaRemainderBits = mantissaBits % 8;
            int mantissaDiscardedBits = 23 - mantissaBits; // ADDED
            valuebits = valuebits >> mantissaDiscardedBits; // ADDED
            if (mantissaRemainderBits != 0)
            {
                WritePartialByte(writer, mantissaRemainderBits, (byte)(valuebits >> (23 - mantissaDiscardedBits - mantissaRemainderBits)), ref bitOffset, ref currentByte); // CHANGED (23 - mantissaRemainderBits) ->(23 - mantissaDiscardedBits - mantissaRemainderBits)
                mantissaBitsToWrite -= mantissaRemainderBits;
            }
            while (mantissaBitsToWrite > 0)
            {
                WritePartialByte(writer, 8, (byte)(valuebits >> (mantissaBitsToWrite - 8)), ref bitOffset, ref currentByte);
                mantissaBitsToWrite -= 8;
            }
        }


        public static float ReadFloatHelper(NetworkReader reader, bool formatSigned, int exponentBits, int biasExponent, int mantissaBits, ref int bitOffset, ref byte currentByte)
        {
            // 32 - bit IEEE 754
            // 1 sign bit, 8 exponent bits, 23 mantissa bits, 127 bias (2^7 - 1)

            int sign = 0;
            if (formatSigned)
            {
                sign = ReadPartialByte(reader, 1, ref bitOffset, ref currentByte);
            }

            int exponentReadValue = ReadPartialByte(reader, exponentBits, ref bitOffset, ref currentByte);
            int exponentValue = exponentReadValue - (int)Math.Pow(2, biasExponent) + 127 + 1;
            int mantissaBitsToRead = mantissaBits;
            int mantissaValue = 0;
            while (true)
            {
                int currentReadCount = Math.Min(8, mantissaBitsToRead);
                mantissaValue = mantissaValue << currentReadCount;
                mantissaValue = mantissaValue | ReadPartialByte(reader, currentReadCount, ref bitOffset, ref currentByte);
                mantissaBitsToRead -= currentReadCount;

                if (mantissaBitsToRead <= 0)
                {
                    break;
                }
            }
            mantissaValue = mantissaValue << (23 - mantissaBits);

            //float fractional = 1 + (mantissaValue / 8388608.0f);
            //result = Mathf.Pow(2, exponentValue) * fractional;
            int reconstructedBits = (sign << 31) | (exponentValue << 23) | mantissaValue;
            return BitConverter.Int32BitsToSingle(reconstructedBits);
        }

        public static void WriteDoubleHelper(NetworkWriter writer, double value, bool formatSigned, int exponentBits, int biasExponent, int mantissaBits, ref int bitOffset, ref byte currentByte)
        {
            // 64-bit IEEE 754
            // 1 sign bit, 11 exponent bits, 52 mantissa bits, 1023 bias (2^10 - 1)
            long valuebits = BitConverter.DoubleToInt64Bits(value);

            // Sign bit
            if (formatSigned)
            {
                WritePartialByte(writer, 1, (byte)(valuebits >> 63), ref bitOffset, ref currentByte);
            }

            // Exponent - following the same pattern as float
            int oldExponent = (int)((valuebits >> 52) & 0x7FF); // 11 bits
            int newExponent = oldExponent - 1023 + (int)Math.Pow(2, biasExponent) - 1;

            // Write exponent bits in chunks (max 8 bits at a time)
            int exponentBitsToWrite = exponentBits;
            while (exponentBitsToWrite > 0)
            {
                int currentWriteCount = Math.Min(8, exponentBitsToWrite);
                int extractedBits = newExponent >> (exponentBitsToWrite - currentWriteCount);
                WritePartialByte(writer, currentWriteCount, (byte)extractedBits, ref bitOffset, ref currentByte);
                exponentBitsToWrite -= currentWriteCount;
            }

            // Mantissa - simplified to match float pattern
            int mantissaBitsToWrite = mantissaBits;
            int mantissaDiscardedBits = 52 - mantissaBits;
            long alignedMantissa = valuebits >> mantissaDiscardedBits;

            while (mantissaBitsToWrite > 0)
            {
                int currentWriteCount = Math.Min(8, mantissaBitsToWrite);
                // Extract the most significant bits of the remaining mantissa
                long extractedBits = alignedMantissa >> (mantissaBitsToWrite - currentWriteCount);
                WritePartialByte(writer, currentWriteCount, (byte)extractedBits, ref bitOffset, ref currentByte);
                mantissaBitsToWrite -= currentWriteCount;
            }
        }

        public static double ReadDoubleHelper(NetworkReader reader, bool formatSigned, int exponentBits, int biasExponent, int mantissaBits, ref int bitOffset, ref byte currentByte)
        {
            // 64-bit IEEE 754
            // 1 sign bit, 11 exponent bits, 52 mantissa bits, 1023 bias (2^10 - 1)

            // Sign bit
            long sign = 0;
            if (formatSigned)
            {
                sign = ReadPartialByte(reader, 1, ref bitOffset, ref currentByte);
            }

            // Exponent
            int exponentBitsToRead = exponentBits;
            int exponentReadValue = 0;
            while (exponentBitsToRead > 0)
            {
                int currentReadCount = Math.Min(8, exponentBitsToRead);
                exponentReadValue = exponentReadValue << currentReadCount;
                exponentReadValue = exponentReadValue | ReadPartialByte(reader, currentReadCount, ref bitOffset, ref currentByte);
                exponentBitsToRead -= currentReadCount;
            }

            int exponentValue = exponentReadValue - (int)Math.Pow(2, biasExponent) + 1023;

            // Mantissa
            int mantissaBitsToRead = mantissaBits;
            long mantissaValue = 0;
            while (mantissaBitsToRead > 0)
            {
                int currentReadCount = Math.Min(8, mantissaBitsToRead);
                mantissaValue = mantissaValue << currentReadCount;
                mantissaValue = mantissaValue | ReadPartialByte(reader, currentReadCount, ref bitOffset, ref currentByte);
                mantissaBitsToRead -= currentReadCount;
            }
            mantissaValue = mantissaValue << (52 - mantissaBits);

            // Reconstruct IEEE 754 representation using bit manipulation
            long reconstructedBits = (sign << 63) | ((long)exponentValue << 52) | mantissaValue;
            return BitConverter.Int64BitsToDouble(reconstructedBits);
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
