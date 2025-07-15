using UnityEngine;
using Mono.CecilX;
using Mirror.Core;
using System;

namespace Mirror.Weaver
{
    public static class BitpackingFormatHelpers
    {
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


        public static BitpackingHelpers.IntegerFormatInfo GetIntegerBitPackedFormat(FieldDefinition field)
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
            BitpackingHelpers.IntegerFormatInfo format = new BitpackingHelpers.IntegerFormatInfo();
            if (min > 0) { format.Signed = false; }
            else { format.Signed = (min < 0); }

            long minMagnitude = min < 0 ? -min : min;
            long maxMagnitude = max < 0 ? -max : max;
            long formatMagnitude = minMagnitude > maxMagnitude ? minMagnitude : maxMagnitude;
            format.Bits = (int)BitpackingHelpers.FindNextPowerOf2Exponent(formatMagnitude) + 1;

            return format;
        }


        public static BitpackingHelpers.DecimalFormatInfo GetFloatFormatInfo(FieldDefinition field, Mirror.Weaver.Logger log)
        {
            return GetDecimalFormatInfo(field, 127, 8, 23, log);
        }
        public static BitpackingHelpers.DecimalFormatInfo GetDoubleFormatInfo(FieldDefinition field, Mirror.Weaver.Logger log)
        {
            return GetDecimalFormatInfo(field, 1023, 11, 52, log);
        }
        static BitpackingHelpers.DecimalFormatInfo GetDecimalFormatInfo(FieldDefinition field, int typeBias, int typeExponentBits, int typeMantissaBits, Mirror.Weaver.Logger log)
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
            BitpackingHelpers.DecimalFormatInfo format = new BitpackingHelpers.DecimalFormatInfo();
            format.Signed = (minValue < 0);
            format.MinPrecision = minPrecision;

            long maxExponent = BitpackingHelpers.FindNextPowerOf2Exponent(maxValue);
            long minExponent = BitpackingHelpers.FindPreviousPowerOf2Exponent(minPrecision);
            Debug.Assert(maxExponent >= minExponent);

            // This is the value encoded in our exponent for the lowest non-zero value we have to encode. Our bias offset cannot be greater than this or our bias conversion will underflow. 
            long minPrecisionExponentValue = (BitConverter.SingleToInt32Bits((float)minPrecision) >> typeMantissaBits) & (LongPow(2, typeExponentBits + 1) - 1); 

            // This is the number of bits we need to represent the exponent range down from lowest non-zero value up to highest value
            ushort bitsToRepresentRange = (ushort)BitpackingHelpers.FindNextPowerOf2Exponent(maxExponent - minExponent);
            format.ExponentBits = Math.Min(typeExponentBits, (ushort)bitsToRepresentRange);
            // We pick a bias so that for our lowest non zero representable value, the exponent encoded value is 1. This leaves open the value 0 to represent... actual 0!
            // And also minimizes the amount of bits needed to encode our largest representable value.
            format.NewBias = typeBias - (int)minPrecisionExponentValue + 1;

            // As long as the mantissa is large enough to achieve our minimum precision for the highest exponent in value range, then it will also be enough for the lower exponent values.
            float HighestIntervalLength = Mathf.Pow(2, maxExponent) - Mathf.Pow(2, maxExponent - 1);
            format.MantissaBits = Math.Min(typeMantissaBits, (ushort)BitpackingHelpers.FindNextPowerOf2Exponent(HighestIntervalLength / minPrecision));

            return format;
        }

        public static long LongPow(long baseNum, long exp)
        {
            if (exp < 0) throw new ArgumentException("Negative exponents not supported");
            if (exp == 0) return 1;

            long result = 1;
            for (int i = 0; i < exp; i++)
            {
                result *= baseNum;
            }
            return result;
        }


    }

}
