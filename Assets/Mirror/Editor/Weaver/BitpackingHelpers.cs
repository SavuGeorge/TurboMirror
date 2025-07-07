using Mono.CecilX;
using UnityEngine;
using System.Collections.Generic;
using System;

namespace Mirror.Weaver
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


        public static bool HasBitpackedAttribute(TypeReference typeRef)
        {
            // Resolve the type reference to get access to custom attributes
            TypeDefinition typeDef = typeRef.Resolve();
            if (typeDef == null)
                return false;

            // Check custom attributes
            foreach (CustomAttribute attr in typeDef.CustomAttributes)
            {
                if (attr.AttributeType.FullName == "Bitpacked" ||
                    attr.AttributeType.Name == "Bitpacked")
                {
                    return true;
                }
            }

            return false;
        }


        public static IntegerFormatInfo GetIntegerBitPackedFormat(FieldDefinition field)
        {
            // === Get the attributes
            long min = -long.MaxValue;
            long max = long.MaxValue;
            foreach (CustomAttribute attr in field.CustomAttributes)
            {
                // Check if this is our BitPackedAttribute
                if (attr.AttributeType.FullName == "Mirror.IntegerBitPackedAttribute" ||
                    attr.Constructor.DeclaringType.Name == "IntegerBitPackedAttribute")
                {
                    if (attr.ConstructorArguments.Count == 2)
                    {
                        min = (int)attr.ConstructorArguments[0].Value;
                        max = (int)attr.ConstructorArguments[1].Value;
                    }
                }
            }

            // === Compute the format
            IntegerFormatInfo format = new IntegerFormatInfo();
            if (min > 0) { format.Signed = false; }
            else { format.Signed = (min < 0); }

            long minMagnitude = min < 0 ? -min : min;
            long maxMagnitude = max < 0 ? -max : max;
            long formatMagnitude = minMagnitude > maxMagnitude ? minMagnitude : maxMagnitude;
            format.Bits = (int)FindNextPowerOf2Exponent(formatMagnitude) + 1;

            return format; 
        }


        public static DecimalFormatInfo GetDecimalFormatInfo(FieldDefinition field)
        {
            // === Get attributes
            double minValue = -double.MaxValue;
            double maxValue = double.MaxValue;
            double minPrecision = double.MinValue;
            foreach (CustomAttribute attr in field.CustomAttributes)
            {
                // Check if this is our DecimalBitPackedAttribute
                if (attr.AttributeType.FullName == "Mirror.DecimalBitPackedAttribute" ||
                    attr.Constructor.DeclaringType.Name == "DecimalBitPackedAttribute")
                {
                    // Get the values from the constructor arguments
                    if (attr.ConstructorArguments.Count == 3)
                    {
                        minValue = (double)attr.ConstructorArguments[0].Value;
                        maxValue = (double)attr.ConstructorArguments[1].Value;
                        minPrecision = (double)attr.ConstructorArguments[2].Value;
                    }
                }
            }

            // === Compute the format 
            DecimalFormatInfo format = new DecimalFormatInfo();
            format.Signed = (minValue < 0);

            long emax = FindNextPowerOf2Exponent(maxValue);
            long emin = FindPreviousPowerOf2Exponent(minPrecision);
            Debug.Assert(emax >= emin);

            format.BiasExponent = (short)emin;
            format.ExponentBits = (ushort)(emax - emin);

            // As long as the mantissa is large enough to achieve our minimum precision for the highest exponent in value range, then it will also be enough for the lower exponent values.
            float HighestIntervalLength = Mathf.Pow(2, emax) - Mathf.Pow(2, emax - 1);
            format.MantissaBits = (ushort)FindNextPowerOf2Exponent(Log2(HighestIntervalLength) - Log2(minPrecision));

            return format;
        }

        static long FindNextPowerOf2Exponent(double x)
        {
            if (x <= 0) throw new System.Exception("X must be positive");
            return (long)System.Math.Ceiling(Log2(x));
        }
        static long FindPreviousPowerOf2Exponent(double x)
        {
            if (x <= 0) throw new System.Exception("X must be positive");
            return (long)System.Math.Floor(Log2(x));
        }

        static double Log2(double x)
        {
            return System.Math.Log(x) / System.Math.Log(2.0f);
        }

        public static void WriteIntegerHelper(NetworkWriter writer, long value, IntegerFormatInfo format, ref int bitOffset, ref byte currentByte)
        {
            int bitsToWrite = format.Bits;
            int remainderBits = bitsToWrite % 8;
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

            if (bits > freeBitsInCurrentByte)
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

    }

}
