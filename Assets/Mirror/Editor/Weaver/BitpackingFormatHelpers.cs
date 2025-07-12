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


        public static BitpackingHelpers.DecimalFormatInfo GetFloatFormatInfo(FieldDefinition field)
        {
            return GetDecimalFormatInfo(field, 7, 8, 23);
        }
        public static BitpackingHelpers.DecimalFormatInfo GetDoubleFormatInfo(FieldDefinition field)
        {
            return GetDecimalFormatInfo(field, 10, 11, 52);
        }
        static BitpackingHelpers.DecimalFormatInfo GetDecimalFormatInfo(FieldDefinition field, int typeBiasExponent, int typeExponentBits, int typeMantissaBits)
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

            // TODO: we usually don't actually even need the next power of two, since the full mantissa gets us most of the way to 2x at full precision, and still most of the way to 2x at even 3-4 bits
            // should be able to figure out based on our precision if we can drop one more bit somehow. 
            long emax = BitpackingHelpers.FindNextPowerOf2Exponent(maxValue);
            long emin = BitpackingHelpers.FindPreviousPowerOf2Exponent(minPrecision);
            format.BiasExponent = (short)Math.Min(typeBiasExponent, -emin + 1); // first things first, we try a lower bias. We probably don't need all the precision we would get from the standard bias
            Debug.Assert(emax >= emin);

            format.ExponentBits = (ushort)(emax - emin - (typeBiasExponent - format.BiasExponent) + 1);
            //format.ExponentBits = (ushort)BitpackingHelpers.FindNextPowerOf2Exponent(exponentRange);
            //format.ExponentBits = (ushort)(emax + format.BiasExponent);

            // As long as the mantissa is large enough to achieve our minimum precision for the highest exponent in value range, then it will also be enough for the lower exponent values.
            float HighestIntervalLength = Mathf.Pow(2, emax) - Mathf.Pow(2, emax - 1);
            format.MantissaBits = (ushort)BitpackingHelpers.FindNextPowerOf2Exponent(HighestIntervalLength / minPrecision);

            return format;
        }
    }

}
