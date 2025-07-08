using UnityEngine;
using Mono.CecilX;
using Mirror.Core;

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


        public static BitpackingHelpers.DecimalFormatInfo GetDecimalFormatInfo(FieldDefinition field)
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

            long emax = BitpackingHelpers.FindNextPowerOf2Exponent(maxValue);
            long emin = BitpackingHelpers.FindPreviousPowerOf2Exponent(minPrecision);
            Debug.Assert(emax >= emin);

            format.BiasExponent = (short)emin;
            format.ExponentBits = (ushort)(emax - emin);

            // As long as the mantissa is large enough to achieve our minimum precision for the highest exponent in value range, then it will also be enough for the lower exponent values.
            float HighestIntervalLength = Mathf.Pow(2, emax) - Mathf.Pow(2, emax - 1);
            format.MantissaBits = (ushort)BitpackingHelpers.FindNextPowerOf2Exponent(BitpackingHelpers.Log2(HighestIntervalLength) - BitpackingHelpers.Log2(minPrecision));

            return format;
        }
    }

}
