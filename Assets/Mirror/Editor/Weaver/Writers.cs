using System;
using System.Collections.Generic;
using Mono.CecilX;
using Mono.CecilX.Cil;
// to use Mono.CecilX.Rocks here, we need to 'override references' in the
// Unity.Mirror.CodeGen assembly definition file in the Editor, and add CecilX.Rocks.
// otherwise we get an unknown import exception.
using Mono.CecilX.Rocks;
using System.Linq;
using Mirror.Core;

namespace Mirror.Weaver
{
    // not static, because ILPostProcessor is multithreaded
    public class Writers
    {
        // Writers are only for this assembly.
        // can't be used from another assembly, otherwise we will get:
        // "System.ArgumentException: Member ... is declared in another module and needs to be imported"
        AssemblyDefinition assembly;
        WeaverTypes weaverTypes;
        TypeDefinition GeneratedCodeClass;
        Logger Log;

        Dictionary<TypeReference, MethodReference> writeFuncs =
            new Dictionary<TypeReference, MethodReference>(new TypeReferenceComparer());

        public Writers(AssemblyDefinition assembly, WeaverTypes weaverTypes, TypeDefinition GeneratedCodeClass, Logger Log)
        {
            this.assembly = assembly;
            this.weaverTypes = weaverTypes;
            this.GeneratedCodeClass = GeneratedCodeClass;
            this.Log = Log;
        }

        public void Register(TypeReference dataType, MethodReference methodReference)
        {
            // sometimes we define multiple write methods for the same type.
            // for example:
            //   WriteInt()     // alwasy writes 4 bytes: should be available to the user for binary protocols etc.
            //   WriteVarInt()  // varint compression: we may want Weaver to always use this for minimal bandwidth
            // give the user a way to define the weaver prefered one if two exists:
            //   "[WeaverPriority]" attribute is automatically detected and prefered.
            MethodDefinition methodDefinition = methodReference.Resolve();
            bool priority = methodDefinition.HasCustomAttribute<WeaverPriorityAttribute>();
            // if (priority) Log.Warning($"Weaver: Registering priority Write<{dataType.FullName}> with {methodReference.FullName}.", methodReference);

            // Weaver sometimes calls Register for <T> multiple times because we resolve assemblies multiple times.
            // if the function name is the same: always use the latest one.
            // if the function name differes: use the priority one.
            if (writeFuncs.TryGetValue(dataType, out MethodReference existingMethod) && // if it was already defined
                existingMethod.FullName != methodReference.FullName && // and this one is a different name
                !priority) // and it's not the priority one
            {
                return; // then skip
            }

            // we need to import type when we Initialize Writers so import here in case it is used anywhere else
            TypeReference imported = assembly.MainModule.ImportReference(dataType);
            writeFuncs[imported] = methodReference;
        }

        void RegisterWriteFunc(TypeReference typeReference, MethodDefinition newWriterFunc)
        {
            Register(typeReference, newWriterFunc);
            GeneratedCodeClass.Methods.Add(newWriterFunc);
        }

        // Finds existing writer for type, if non exists trys to create one
        public MethodReference GetWriteFunc(TypeReference variable, ref bool WeavingFailed)
        {
            if (writeFuncs.TryGetValue(variable, out MethodReference foundFunc))
                return foundFunc;

            // this try/catch will be removed in future PR and make `GetWriteFunc` throw instead
            try
            {
                TypeReference importedVariable = assembly.MainModule.ImportReference(variable);
                return GenerateWriter(importedVariable, ref WeavingFailed);
            }
            catch (GenerateWriterException e)
            {
                Log.Error(e.Message, e.MemberReference);
                WeavingFailed = true;
                return null;
            }
        }

        //Throws GenerateWriterException when writer could not be generated for type
        MethodReference GenerateWriter(TypeReference variableReference, ref bool WeavingFailed)
        {
            if (variableReference.IsByReference)
            {
                throw new GenerateWriterException($"Cannot pass {variableReference.Name} by reference", variableReference);
            }

            // Arrays are special, if we resolve them, we get the element type,
            // e.g. int[] resolves to int
            // therefore process this before checks below
            if (variableReference.IsArray)
            {
                if (variableReference.IsMultidimensionalArray())
                {
                    throw new GenerateWriterException($"{variableReference.Name} is an unsupported type. Multidimensional arrays are not supported", variableReference);
                }
                TypeReference elementType = variableReference.GetElementType();
                return GenerateCollectionWriter(variableReference, elementType, nameof(NetworkWriterExtensions.WriteArray), ref WeavingFailed);
            }

            if (variableReference.Resolve()?.IsEnum ?? false)
            {
                // serialize enum as their base type
                return GenerateEnumWriteFunc(variableReference, ref WeavingFailed);
            }

            // check for collections
            if (variableReference.Is(typeof(ArraySegment<>)))
            {
                GenericInstanceType genericInstance = (GenericInstanceType)variableReference;
                TypeReference elementType = genericInstance.GenericArguments[0];

                return GenerateCollectionWriter(variableReference, elementType, nameof(NetworkWriterExtensions.WriteArraySegment), ref WeavingFailed);
            }
            if (variableReference.Is(typeof(List<>)))
            {
                GenericInstanceType genericInstance = (GenericInstanceType)variableReference;
                TypeReference elementType = genericInstance.GenericArguments[0];

                return GenerateCollectionWriter(variableReference, elementType, nameof(NetworkWriterExtensions.WriteList), ref WeavingFailed);
            }
            if (variableReference.Is(typeof(HashSet<>)))
            {
                GenericInstanceType genericInstance = (GenericInstanceType)variableReference;
                TypeReference elementType = genericInstance.GenericArguments[0];

                return GenerateCollectionWriter(variableReference, elementType, nameof(NetworkWriterExtensions.WriteHashSet), ref WeavingFailed);
            }

            // handle both NetworkBehaviour and inheritors.
            // fixes: https://github.com/MirrorNetworking/Mirror/issues/2939
            if (variableReference.IsDerivedFrom<NetworkBehaviour>() || variableReference.Is<NetworkBehaviour>())
            {
                return GetNetworkBehaviourWriter(variableReference);
            }

            // check for invalid types
            TypeDefinition variableDefinition = variableReference.Resolve();
            if (variableDefinition == null)
            {
                throw new GenerateWriterException($"{variableReference.Name} is not a supported type. Use a supported type or provide a custom writer", variableReference);
            }
            if (variableDefinition.IsDerivedFrom<UnityEngine.Component>())
            {
                throw new GenerateWriterException($"Cannot generate writer for component type {variableReference.Name}. Use a supported type or provide a custom writer", variableReference);
            }
            if (variableReference.Is<UnityEngine.Object>())
            {
                throw new GenerateWriterException($"Cannot generate writer for {variableReference.Name}. Use a supported type or provide a custom writer", variableReference);
            }
            if (variableReference.Is<UnityEngine.ScriptableObject>())
            {
                throw new GenerateWriterException($"Cannot generate writer for {variableReference.Name}. Use a supported type or provide a custom writer", variableReference);
            }
            if (variableDefinition.HasGenericParameters)
            {
                throw new GenerateWriterException($"Cannot generate writer for generic type {variableReference.Name}. Use a supported type or provide a custom writer", variableReference);
            }
            if (variableDefinition.IsInterface)
            {
                throw new GenerateWriterException($"Cannot generate writer for interface {variableReference.Name}. Use a supported type or provide a custom writer", variableReference);
            }
            if (variableDefinition.IsAbstract)
            {
                throw new GenerateWriterException($"Cannot generate writer for abstract class {variableReference.Name}. Use a supported type or provide a custom writer", variableReference);
            }

            // generate writer for class/struct
            return GenerateClassOrStructWriterFunction(variableReference, ref WeavingFailed);
        }

        MethodReference GetNetworkBehaviourWriter(TypeReference variableReference)
        {
            // all NetworkBehaviours can use the same write function
            if (writeFuncs.TryGetValue(weaverTypes.Import<NetworkBehaviour>(), out MethodReference func))
            {
                // register function so it is added to writer<T>
                // use Register instead of RegisterWriteFunc because this is not a generated function
                Register(variableReference, func);

                return func;
            }
            else
            {
                // this exception only happens if mirror is missing the WriteNetworkBehaviour method
                throw new MissingMethodException($"Could not find writer for NetworkBehaviour");
            }
        }

        MethodDefinition GenerateEnumWriteFunc(TypeReference variable, ref bool WeavingFailed)
        {
            MethodDefinition writerFunc = GenerateWriterFunc(variable);

            ILProcessor worker = writerFunc.Body.GetILProcessor();

            MethodReference underlyingWriter = GetWriteFunc(variable.Resolve().GetEnumUnderlyingType(), ref WeavingFailed);

            worker.Emit(OpCodes.Ldarg_0);
            worker.Emit(OpCodes.Ldarg_1);
            worker.Emit(OpCodes.Call, underlyingWriter);

            worker.Emit(OpCodes.Ret);
            return writerFunc;
        }

        MethodDefinition GenerateWriterFunc(TypeReference variable)
        {
            string functionName = $"_Write_{variable.FullName}";
            // create new writer for this type
            MethodDefinition writerFunc = new MethodDefinition(functionName,
                    MethodAttributes.Public |
                    MethodAttributes.Static |
                    MethodAttributes.HideBySig,
                    weaverTypes.Import(typeof(void)));

            writerFunc.Parameters.Add(new ParameterDefinition("writer", ParameterAttributes.None, weaverTypes.Import<NetworkWriter>()));
            writerFunc.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, variable));
            writerFunc.Body.InitLocals = true;

            RegisterWriteFunc(variable, writerFunc);
            return writerFunc;
        }

        MethodDefinition GenerateClassOrStructWriterFunction(TypeReference variable, ref bool WeavingFailed)
        {
            MethodDefinition writerFunc = GenerateWriterFunc(variable);

            ILProcessor worker = writerFunc.Body.GetILProcessor();

            bool isBitpackedStruct = BitpackingFormatHelpers.HasBitpackedAttribute(variable);
            if (isBitpackedStruct)
            {
                if (!WriteAllFieldsBitpacked(variable, worker, ref WeavingFailed))
                    return null;
            }
            else
            {
                if (!variable.Resolve().IsValueType)
                    WriteNullCheck(worker, ref WeavingFailed);

                if (!WriteAllFieldsClassic(variable, worker, ref WeavingFailed))
                    return null;
            }

            worker.Emit(OpCodes.Ret);
            return writerFunc;
        }

        void WriteNullCheck(ILProcessor worker, ref bool WeavingFailed)
        {
            // if (value == null)
            // {
            //     writer.WriteBoolean(false);
            //     return;
            // }
            //

            Instruction labelNotNull = worker.Create(OpCodes.Nop);
            worker.Emit(OpCodes.Ldarg_1);
            worker.Emit(OpCodes.Brtrue, labelNotNull);
            worker.Emit(OpCodes.Ldarg_0);
            worker.Emit(OpCodes.Ldc_I4_0);
            worker.Emit(OpCodes.Call, GetWriteFunc(weaverTypes.Import<bool>(), ref WeavingFailed));
            worker.Emit(OpCodes.Ret);
            worker.Append(labelNotNull);

            // write.WriteBoolean(true);
            worker.Emit(OpCodes.Ldarg_0);
            worker.Emit(OpCodes.Ldc_I4_1);
            worker.Emit(OpCodes.Call, GetWriteFunc(weaverTypes.Import<bool>(), ref WeavingFailed));
        }

        bool WriteAllFieldsClassic(TypeReference variable, ILProcessor worker, ref bool WeavingFailed)
        {
            foreach (FieldDefinition field in variable.FindAllPublicFields())
            {
                MethodReference writeFunc = GetWriteFunc(field.FieldType, ref WeavingFailed);
                // need this null check till later PR when GetWriteFunc throws exception instead
                if (writeFunc == null) { return false; }

                FieldReference fieldRef = assembly.MainModule.ImportReference(field);

                worker.Emit(OpCodes.Ldarg_0);
                worker.Emit(OpCodes.Ldarg_1);
                worker.Emit(OpCodes.Ldfld, fieldRef);
                worker.Emit(OpCodes.Call, writeFunc);
            }

            return true;
        }


        // Find all fields in type and write them
        bool WriteAllFieldsBitpacked(TypeReference variable, ILProcessor worker, ref bool WeavingFailed)
        {
            int weaverBitCounter = 0; // keeping track of how many bits we actually write, so we know if the type needs a final flush instruction or not

            MethodDefinition method = worker.Body.Method;

            // Add local variable for byte currentByte
            TypeReference byteType = assembly.MainModule.ImportReference(typeof(byte));
            VariableDefinition currentByteVar = new VariableDefinition(byteType);
            method.Body.Variables.Add(currentByteVar);
            int currentByteVarIndex = method.Body.Variables.Count - 1;

            // Add local variable for int bitOffset
            TypeReference intType = assembly.MainModule.ImportReference(typeof(int));
            VariableDefinition bitOffsetVar = new VariableDefinition(intType);
            method.Body.Variables.Add(bitOffsetVar);
            int bitOffsetVarIndex = method.Body.Variables.Count - 1;

            // Generate IL for: byte currentByte = 0;
            worker.Emit(OpCodes.Ldc_I4_0);
            worker.Emit(OpCodes.Stloc, currentByteVarIndex);

            // Generate IL for: int bitOffset = 0;
            worker.Emit(OpCodes.Ldc_I4_0);
            worker.Emit(OpCodes.Stloc, bitOffsetVarIndex);

            /// Above code generates IL equivalent of: 
            //byte currentByte = 0;
            //int bitOffset = 0;

            TypeReference bitpackingHelpersType = assembly.MainModule.ImportReference(typeof(BitpackingHelpers));
            MethodReference writePartialByteRef = Resolvers.ResolveMethod(
                bitpackingHelpersType, assembly, Log, "WritePartialByte", ref WeavingFailed);

            foreach (FieldDefinition field in variable.FindAllPublicFields())
            {
                string typeName;
                if (field.FieldType.Resolve().IsEnum)
                {
                    typeName = field.FieldType.Resolve().GetEnumUnderlyingType().FullName;
                }
                else
                {
                    typeName = field.FieldType.FullName;
                }

                FieldReference fieldRef = assembly.MainModule.ImportReference(field);

                switch (typeName)
                {
                    case "System.Boolean":
                        weaverBitCounter += 1;

                        // Bitpacks one bit, pretty straightforward
                        // equivalent to: 
                        // BitpackingHelpers.WritePartialByte(writer, 1, value ? 1 : 0, ref bitOffset, ref currentByte);
                        worker.Emit(OpCodes.Ldarg_0);                      // Load writer (first param of generated method)
                        worker.Emit(OpCodes.Ldc_I4_1);                     // Load 1 (bits to write)

                        // Load field value and convert to byte
                        worker.Emit(OpCodes.Ldarg_1);                      // Load value object
                        worker.Emit(OpCodes.Ldfld, field);                 // Get bool field

                        worker.Emit(OpCodes.Ldloca, bitOffsetVarIndex);    // Load address of bitOffset
                        worker.Emit(OpCodes.Ldloca, currentByteVarIndex);  // Load address of currentByte

                        worker.Emit(OpCodes.Call, writePartialByteRef);
                        break;

                    case "System.Byte":
                    case "System.SByte":
                    case "System.UInt16":
                    case "System.Int16":
                    case "System.UInt32":
                    case "System.Int32":
                    case "System.UInt64":
                    case "System.Int64":
                        BitpackingHelpers.IntegerFormatInfo integerFormat = BitpackingFormatHelpers.GetIntegerBitPackedFormat(field);
                        weaverBitCounter += integerFormat.Bits + (integerFormat.Signed ? 1 : 0);

                        // Resolve the specific WriteIntegerHelper overload and get type info
                        MethodReference writeIntegerHelperRef;
                        int integerTypeBits;
                        bool isSignedType;

                        switch (typeName)
                        {
                            case "System.Byte":
                                writeIntegerHelperRef = Resolvers.ResolveMethod(bitpackingHelpersType, assembly, Log,
                                    "WriteIntegerHelperByte", ref WeavingFailed);
                                integerTypeBits = 8;
                                isSignedType = false;
                                break;
                            case "System.SByte":
                                writeIntegerHelperRef = Resolvers.ResolveMethod(bitpackingHelpersType, assembly, Log,
                                    "WriteIntegerHelperSByte", ref WeavingFailed);
                                integerTypeBits = 8;
                                isSignedType = true;
                                break;
                            case "System.UInt16":
                                writeIntegerHelperRef = Resolvers.ResolveMethod(bitpackingHelpersType, assembly, Log,
                                    "WriteIntegerHelperUShort", ref WeavingFailed);
                                integerTypeBits = 16;
                                isSignedType = false;
                                break;
                            case "System.Int16":
                                writeIntegerHelperRef = Resolvers.ResolveMethod(bitpackingHelpersType, assembly, Log,
                                    "WriteIntegerHelperShort", ref WeavingFailed);
                                integerTypeBits = 16;
                                isSignedType = true;
                                break;
                            case "System.UInt32":
                                writeIntegerHelperRef = Resolvers.ResolveMethod(bitpackingHelpersType, assembly, Log,
                                    "WriteIntegerHelperUInt", ref WeavingFailed);
                                integerTypeBits = 32;
                                isSignedType = false;
                                break;
                            case "System.Int32":
                                writeIntegerHelperRef = Resolvers.ResolveMethod(bitpackingHelpersType, assembly, Log,
                                    "WriteIntegerHelperInt", ref WeavingFailed);
                                integerTypeBits = 32;
                                isSignedType = true;
                                break;
                            case "System.UInt64":
                                writeIntegerHelperRef = Resolvers.ResolveMethod(bitpackingHelpersType, assembly, Log,
                                    "WriteIntegerHelperULong", ref WeavingFailed);
                                integerTypeBits = 64;
                                isSignedType = false;
                                break;
                            case "System.Int64":
                                writeIntegerHelperRef = Resolvers.ResolveMethod(bitpackingHelpersType, assembly, Log,
                                    "WriteIntegerHelperLong", ref WeavingFailed);
                                integerTypeBits = 64;
                                isSignedType = true;
                                break;
                            default:
                                throw new ArgumentException($"Unknown integer type: {typeName}");
                        }


                        // Generate IL: BitpackingHelpers.WriteIntegerHelper(writer, value, typeBits, formatSigned, formatBits, ref bitOffset, ref currentByte);
                        worker.Emit(OpCodes.Ldarg_0);                                                               // Load writer
                        worker.Emit(OpCodes.Ldarg_1);                                                               // Load struct
                        worker.Emit(OpCodes.Ldfld, fieldRef);                                                       // Load field value
                        worker.Emit(OpCodes.Ldc_I4, integerTypeBits);                                               // Load typeBits
                        worker.Emit((isSignedType && integerFormat.Signed) ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);  // Load formatSigned
                        worker.Emit(OpCodes.Ldc_I4, integerFormat.Bits);                                            // Load formatBits
                        worker.Emit(OpCodes.Ldloca, bitOffsetVarIndex);                                             // Load address of bitOffset
                        worker.Emit(OpCodes.Ldloca, currentByteVarIndex);                                           // Load address of currentByte
                        worker.Emit(OpCodes.Call, writeIntegerHelperRef);                                           // Call helper
                        break;


                    case "System.Single":
                    case "System.Double":

                        string helperMethodName;
                        BitpackingHelpers.DecimalFormatInfo decimalFormat;
                        if (typeName == "System.Single")
                        {
                            helperMethodName = "WriteFloatHelper";
                            decimalFormat = BitpackingFormatHelpers.GetFloatFormatInfo(field, Log);
                        }
                        else // if(typeName == "System.Double")
                        {
                            helperMethodName = "WriteDoubleHelper";
                            decimalFormat = BitpackingFormatHelpers.GetDoubleFormatInfo(field, Log);
                        }

                        weaverBitCounter += decimalFormat.ExponentBits + decimalFormat.MantissaBits + (decimalFormat.Signed ? 1 : 0);

                        // Resolve the helper method
                        MethodReference writeHelperRef = Resolvers.ResolveMethod(
                            bitpackingHelpersType, assembly, Log, helperMethodName, ref WeavingFailed);

                        // Generate IL: BitpackingHelpers.WriteXXXHelper(writer, value, formatSigned, exponentBits, biasExponent, mantissaBits, minPrecision, ref bitOffset, ref currentByte);
                        worker.Emit(OpCodes.Ldarg_0);                                            // Load writer
                        worker.Emit(OpCodes.Ldarg_1);                                            // Load struct
                        worker.Emit(OpCodes.Ldfld, fieldRef);                                    // Load field value
                        worker.Emit(decimalFormat.Signed ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); // Load formatSigned 
                        worker.Emit(OpCodes.Ldc_I4, decimalFormat.ExponentBits);                 // Load exponentBits
                        worker.Emit(OpCodes.Ldc_I4, decimalFormat.NewBias);                      // Load biasOffset
                        worker.Emit(OpCodes.Ldc_I4, decimalFormat.MantissaBits);                 // Load mantissaBits
                        worker.Emit(OpCodes.Ldc_R8, decimalFormat.MinPrecision);                 // Load minPrecision (double)
                        worker.Emit(OpCodes.Ldloca, bitOffsetVarIndex);                          // Load address of bitOffset
                        worker.Emit(OpCodes.Ldloca, currentByteVarIndex);                        // Load address of currentByte
                        worker.Emit(OpCodes.Call, writeHelperRef);                               // Call helper
                        break;


                    default:
                        WeavingFailed = true;
                        throw new NotSupportedException($"Field type '{typeName}' is not currently supported for bit-packing serialization");
                }
            }

            if (weaverBitCounter % 8 != 0) // we know at weave-time if we're gonna have a left-over partial byte or not
            {
                worker.Emit(OpCodes.Ldarg_0); // writer
                worker.Emit(OpCodes.Ldloc, currentByteVarIndex); // currentByte
                MethodReference writeByteFunc = GetWriteFunc(weaverTypes.Import<byte>(), ref WeavingFailed);
                worker.Emit(OpCodes.Call, writeByteFunc);
            }

            return true; 
        }

   
        MethodDefinition GenerateCollectionWriter(TypeReference variable, TypeReference elementType, string writerFunction, ref bool WeavingFailed)
        {
            MethodDefinition writerFunc = GenerateWriterFunc(variable);

            MethodReference elementWriteFunc = GetWriteFunc(elementType, ref WeavingFailed);
            MethodReference intWriterFunc = GetWriteFunc(weaverTypes.Import<int>(), ref WeavingFailed);

            // need this null check till later PR when GetWriteFunc throws exception instead
            if (elementWriteFunc == null)
            {
                Log.Error($"Cannot generate writer for {variable}. Use a supported type or provide a custom writer", variable);
                WeavingFailed = true;
                return writerFunc;
            }

            ModuleDefinition module = assembly.MainModule;
            TypeReference readerExtensions = module.ImportReference(typeof(NetworkWriterExtensions));
            MethodReference collectionWriter = Resolvers.ResolveMethod(readerExtensions, assembly, Log, writerFunction, ref WeavingFailed);

            GenericInstanceMethod methodRef = new GenericInstanceMethod(collectionWriter);
            methodRef.GenericArguments.Add(elementType);

            // generates
            // reader.WriteArray<T>(array);

            ILProcessor worker = writerFunc.Body.GetILProcessor();
            worker.Emit(OpCodes.Ldarg_0); // writer
            worker.Emit(OpCodes.Ldarg_1); // collection

            worker.Emit(OpCodes.Call, methodRef); // WriteArray

            worker.Emit(OpCodes.Ret);

            return writerFunc;
        }

        // Save a delegate for each one of the writers into Writer{T}.write
        internal void InitializeWriters(ILProcessor worker)
        {
            ModuleDefinition module = assembly.MainModule;

            TypeReference genericWriterClassRef = module.ImportReference(typeof(Writer<>));

            System.Reflection.FieldInfo fieldInfo = typeof(Writer<>).GetField(nameof(Writer<object>.write));
            FieldReference fieldRef = module.ImportReference(fieldInfo);
            TypeReference networkWriterRef = module.ImportReference(typeof(NetworkWriter));
            TypeReference actionRef = module.ImportReference(typeof(Action<,>));
            MethodReference actionConstructorRef = module.ImportReference(typeof(Action<,>).GetConstructors()[0]);

            foreach (KeyValuePair<TypeReference, MethodReference> kvp in writeFuncs)
            {
                TypeReference targetType = kvp.Key;
                MethodReference writeFunc = kvp.Value;

                // create a Action<NetworkWriter, T> delegate
                worker.Emit(OpCodes.Ldnull);
                worker.Emit(OpCodes.Ldftn, writeFunc);
                GenericInstanceType actionGenericInstance = actionRef.MakeGenericInstanceType(networkWriterRef, targetType);
                MethodReference actionRefInstance = actionConstructorRef.MakeHostInstanceGeneric(assembly.MainModule, actionGenericInstance);
                worker.Emit(OpCodes.Newobj, actionRefInstance);

                // save it in Writer<T>.write
                GenericInstanceType genericInstance = genericWriterClassRef.MakeGenericInstanceType(targetType);
                FieldReference specializedField = fieldRef.SpecializeField(assembly.MainModule, genericInstance);
                worker.Emit(OpCodes.Stsfld, specializedField);
            }
        }
    }
}
