using System;
using System.Collections.Generic;
using Mono.CecilX;
using Mono.CecilX.Cil;
// to use Mono.CecilX.Rocks here, we need to 'override references' in the
// Unity.Mirror.CodeGen assembly definition file in the Editor, and add CecilX.Rocks.
// otherwise we get an unknown import exception.
using Mono.CecilX.Rocks;
using Mirror.Core;

namespace Mirror.Weaver
{
    // not static, because ILPostProcessor is multithreaded
    public class Readers
    {
        // Readers are only for this assembly.
        // can't be used from another assembly, otherwise we will get:
        // "System.ArgumentException: Member ... is declared in another module and needs to be imported"
        AssemblyDefinition assembly;
        WeaverTypes weaverTypes;
        TypeDefinition GeneratedCodeClass;
        Logger Log;

        Dictionary<TypeReference, MethodReference> readFuncs =
            new Dictionary<TypeReference, MethodReference>(new TypeReferenceComparer());

        public Readers(AssemblyDefinition assembly, WeaverTypes weaverTypes, TypeDefinition GeneratedCodeClass, Logger Log)
        {
            this.assembly = assembly;
            this.weaverTypes = weaverTypes;
            this.GeneratedCodeClass = GeneratedCodeClass;
            this.Log = Log;
        }

        internal void Register(TypeReference dataType, MethodReference methodReference)
        {
            // sometimes we define multiple read methods for the same type.
            // for example:
            //   ReadInt()     // alwasy writes 4 bytes: should be available to the user for binary protocols etc.
            //   ReadVarInt()  // varint compression: we may want Weaver to always use this for minimal bandwidth
            // give the user a way to define the weaver prefered one if two exists:
            //   "[WeaverPriority]" attribute is automatically detected and prefered.
            MethodDefinition methodDefinition = methodReference.Resolve();
            bool priority = methodDefinition.HasCustomAttribute<WeaverPriorityAttribute>();
            // if (priority) Log.Warning($"Weaver: Registering priority Read<{dataType.FullName}> with {methodReference.FullName}.", methodReference);

            // Weaver sometimes calls Register for <T> multiple times because we resolve assemblies multiple times.
            // if the function name is the same: always use the latest one.
            // if the function name differes: use the priority one.
            if (readFuncs.TryGetValue(dataType, out MethodReference existingMethod) && // if it was already defined
                existingMethod.FullName != methodReference.FullName && // and this one is a different name
                !priority) // and it's not the priority one
            {
                return; // then skip
            }

            // we need to import type when we Initialize Readers so import here in case it is used anywhere else
            TypeReference imported = assembly.MainModule.ImportReference(dataType);
            readFuncs[imported] = methodReference;
        }

        void RegisterReadFunc(TypeReference typeReference, MethodDefinition newReaderFunc)
        {
            Register(typeReference, newReaderFunc);
            GeneratedCodeClass.Methods.Add(newReaderFunc);
        }

        // Finds existing reader for type, if non exists trys to create one
        public MethodReference GetReadFunc(TypeReference variable, ref bool WeavingFailed)
        {
            if (readFuncs.TryGetValue(variable, out MethodReference foundFunc))
                return foundFunc;

            TypeReference importedVariable = assembly.MainModule.ImportReference(variable);
            return GenerateReader(importedVariable, ref WeavingFailed);
        }

        MethodReference GenerateReader(TypeReference variableReference, ref bool WeavingFailed)
        {
            // Arrays are special,  if we resolve them, we get the element type,
            // so the following ifs might choke on it for scriptable objects
            // or other objects that require a custom serializer
            // thus check if it is an array and skip all the checks.
            if (variableReference.IsArray)
            {
                if (variableReference.IsMultidimensionalArray())
                {
                    Log.Error($"{variableReference.Name} is an unsupported type. Multidimensional arrays are not supported", variableReference);
                    WeavingFailed = true;
                    return null;
                }

                return GenerateReadCollection(variableReference, variableReference.GetElementType(), nameof(NetworkReaderExtensions.ReadArray), ref WeavingFailed);
            }

            TypeDefinition variableDefinition = variableReference.Resolve();

            // check if the type is completely invalid
            if (variableDefinition == null)
            {
                Log.Error($"{variableReference.Name} is not a supported type", variableReference);
                WeavingFailed = true;
                return null;
            }
            else if (variableReference.IsByReference)
            {
                // error??
                Log.Error($"Cannot pass type {variableReference.Name} by reference", variableReference);
                WeavingFailed = true;
                return null;
            }

            // use existing func for known types
            if (variableDefinition.IsEnum)
            {
                return GenerateEnumReadFunc(variableReference, ref WeavingFailed);
            }
            else if (variableDefinition.Is(typeof(ArraySegment<>)))
            {
                return GenerateArraySegmentReadFunc(variableReference, ref WeavingFailed);
            }
            else if (variableDefinition.Is(typeof(List<>)))
            {
                GenericInstanceType genericInstance = (GenericInstanceType)variableReference;
                TypeReference elementType = genericInstance.GenericArguments[0];

                return GenerateReadCollection(variableReference, elementType, nameof(NetworkReaderExtensions.ReadList), ref WeavingFailed);
            }
            else if (variableDefinition.Is(typeof(HashSet<>)))
            {
                GenericInstanceType genericInstance = (GenericInstanceType)variableReference;
                TypeReference elementType = genericInstance.GenericArguments[0];

                return GenerateReadCollection(variableReference, elementType, nameof(NetworkReaderExtensions.ReadHashSet), ref WeavingFailed);
            }
            // handle both NetworkBehaviour and inheritors.
            // fixes: https://github.com/MirrorNetworking/Mirror/issues/2939
            else if (variableReference.IsDerivedFrom<NetworkBehaviour>() || variableReference.Is<NetworkBehaviour>())
            {
                return GetNetworkBehaviourReader(variableReference);
            }

            // check if reader generation is applicable on this type
            if (variableDefinition.IsDerivedFrom<UnityEngine.Component>())
            {
                Log.Error($"Cannot generate reader for component type {variableReference.Name}. Use a supported type or provide a custom reader", variableReference);
                WeavingFailed = true;
                return null;
            }
            if (variableReference.Is<UnityEngine.Object>())
            {
                Log.Error($"Cannot generate reader for {variableReference.Name}. Use a supported type or provide a custom reader", variableReference);
                WeavingFailed = true;
                return null;
            }
            if (variableReference.Is<UnityEngine.ScriptableObject>())
            {
                Log.Error($"Cannot generate reader for {variableReference.Name}. Use a supported type or provide a custom reader", variableReference);
                WeavingFailed = true;
                return null;
            }
            if (variableDefinition.HasGenericParameters)
            {
                Log.Error($"Cannot generate reader for generic variable {variableReference.Name}. Use a supported type or provide a custom reader", variableReference);
                WeavingFailed = true;
                return null;
            }
            if (variableDefinition.IsInterface)
            {
                Log.Error($"Cannot generate reader for interface {variableReference.Name}. Use a supported type or provide a custom reader", variableReference);
                WeavingFailed = true;
                return null;
            }
            if (variableDefinition.IsAbstract)
            {
                Log.Error($"Cannot generate reader for abstract class {variableReference.Name}. Use a supported type or provide a custom reader", variableReference);
                WeavingFailed = true;
                return null;
            }

            return GenerateClassOrStructReadFunction(variableReference, ref WeavingFailed);
        }

        MethodReference GetNetworkBehaviourReader(TypeReference variableReference)
        {
            // uses generic ReadNetworkBehaviour rather than having weaver create one for each NB
            MethodReference generic = weaverTypes.readNetworkBehaviourGeneric;

            MethodReference readFunc = generic.MakeGeneric(assembly.MainModule, variableReference);

            // register function so it is added to Reader<T>
            // use Register instead of RegisterWriteFunc because this is not a generated function
            Register(variableReference, readFunc);

            return readFunc;
        }

        MethodDefinition GenerateEnumReadFunc(TypeReference variable, ref bool WeavingFailed)
        {
            MethodDefinition readerFunc = GenerateReaderFunction(variable);

            ILProcessor worker = readerFunc.Body.GetILProcessor();

            worker.Emit(OpCodes.Ldarg_0);

            TypeReference underlyingType = variable.Resolve().GetEnumUnderlyingType();
            MethodReference underlyingFunc = GetReadFunc(underlyingType, ref WeavingFailed);

            worker.Emit(OpCodes.Call, underlyingFunc);
            worker.Emit(OpCodes.Ret);
            return readerFunc;
        }

        MethodDefinition GenerateArraySegmentReadFunc(TypeReference variable, ref bool WeavingFailed)
        {
            GenericInstanceType genericInstance = (GenericInstanceType)variable;
            TypeReference elementType = genericInstance.GenericArguments[0];

            MethodDefinition readerFunc = GenerateReaderFunction(variable);

            ILProcessor worker = readerFunc.Body.GetILProcessor();

            // $array = reader.Read<[T]>()
            ArrayType arrayType = elementType.MakeArrayType();
            worker.Emit(OpCodes.Ldarg_0);
            worker.Emit(OpCodes.Call, GetReadFunc(arrayType, ref WeavingFailed));

            // return new ArraySegment<T>($array);
            worker.Emit(OpCodes.Newobj, weaverTypes.ArraySegmentConstructorReference.MakeHostInstanceGeneric(assembly.MainModule, genericInstance));
            worker.Emit(OpCodes.Ret);
            return readerFunc;
        }

        MethodDefinition GenerateReaderFunction(TypeReference variable)
        {
            string functionName = $"_Read_{variable.FullName}";

            // create new reader for this type
            MethodDefinition readerFunc = new MethodDefinition(functionName,
                    MethodAttributes.Public |
                    MethodAttributes.Static |
                    MethodAttributes.HideBySig,
                    variable);

            readerFunc.Parameters.Add(new ParameterDefinition("reader", ParameterAttributes.None, weaverTypes.Import<NetworkReader>()));
            readerFunc.Body.InitLocals = true;
            RegisterReadFunc(variable, readerFunc);

            return readerFunc;
        }

        MethodDefinition GenerateReadCollection(TypeReference variable, TypeReference elementType, string readerFunction, ref bool WeavingFailed)
        {
            MethodDefinition readerFunc = GenerateReaderFunction(variable);
            // generate readers for the element
            GetReadFunc(elementType, ref WeavingFailed);

            ModuleDefinition module = assembly.MainModule;
            TypeReference readerExtensions = module.ImportReference(typeof(NetworkReaderExtensions));
            MethodReference listReader = Resolvers.ResolveMethod(readerExtensions, assembly, Log, readerFunction, ref WeavingFailed);

            GenericInstanceMethod methodRef = new GenericInstanceMethod(listReader);
            methodRef.GenericArguments.Add(elementType);

            // generates
            // return reader.ReadList<T>();

            ILProcessor worker = readerFunc.Body.GetILProcessor();
            worker.Emit(OpCodes.Ldarg_0); // reader
            worker.Emit(OpCodes.Call, methodRef); // Read

            worker.Emit(OpCodes.Ret);

            return readerFunc;
        }

        MethodDefinition GenerateClassOrStructReadFunction(TypeReference variable, ref bool WeavingFailed)
        {
            MethodDefinition readerFunc = GenerateReaderFunction(variable);

            // create local for return value
            readerFunc.Body.Variables.Add(new VariableDefinition(variable));

            ILProcessor worker = readerFunc.Body.GetILProcessor();

            TypeDefinition td = variable.Resolve();

            if (!td.IsValueType)
                GenerateNullCheck(worker, ref WeavingFailed);

            CreateNew(variable, worker, td, ref WeavingFailed);

            bool isBitpackedStruct = BitpackingFormatHelpers.HasBitpackedAttribute(variable);
            if (isBitpackedStruct)
            {
                if (!ReadAllFieldsBitpacked(variable, worker, ref WeavingFailed))
                    return null;
            }
            else
            {
                ReadAllFields(variable, worker, ref WeavingFailed);
            }


            worker.Emit(OpCodes.Ldloc_0);
            worker.Emit(OpCodes.Ret);
            return readerFunc;
        }


        bool ReadAllFieldsBitpacked(TypeReference variable, ILProcessor worker, ref bool WeavingFailed)
        {
            int weaverBitCounter = 0; // Track total bits to know if we need final byte read

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

            // Initialize: byte currentByte = 0; int bitOffset = 0;
            worker.Emit(OpCodes.Ldc_I4_0);
            worker.Emit(OpCodes.Stloc, currentByteVarIndex);
            worker.Emit(OpCodes.Ldc_I4_0);
            worker.Emit(OpCodes.Stloc, bitOffsetVarIndex);

            TypeReference bitpackingHelpersType = assembly.MainModule.ImportReference(typeof(BitpackingHelpers));
            MethodReference readPartialByteRef = Resolvers.ResolveMethod(
                bitpackingHelpersType, assembly, Log, "ReadPartialByte", ref WeavingFailed);

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

                switch (typeName)
                {
                    case "System.Boolean":
                        weaverBitCounter += 1;

                        // Load the struct (address for value types, instance for reference types)
                        worker.Emit(variable.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, 0);

                        // Call: BitpackingHelpers.ReadPartialByte(reader, 1, ref bitOffset, ref currentByte)
                        worker.Emit(OpCodes.Ldarg_0);                      // Load reader
                        worker.Emit(OpCodes.Ldc_I4_1);                     // Load 1 (bits to read)
                        worker.Emit(OpCodes.Ldloca, bitOffsetVarIndex);    // Load address of bitOffset
                        worker.Emit(OpCodes.Ldloca, currentByteVarIndex);  // Load address of currentByte
                        worker.Emit(OpCodes.Call, readPartialByteRef);

                        // Convert byte result to bool and store in field
                        worker.Emit(OpCodes.Ldc_I4_0);
                        worker.Emit(OpCodes.Cgt_Un);                       // Convert to bool (1 becomes true, 0 becomes false)

                        worker.Emit(OpCodes.Stfld, assembly.MainModule.ImportReference(field));
                        break;

                    case "System.Byte":
                    case "System.SByte":
                    case "System.UInt16":
                    case "System.Int16":
                    case "System.UInt32":
                    case "System.Int32":
                    case "System.UInt64":
                    case "System.Int64":
                        BitpackingHelpers.IntegerFormatInfo formatInfo = BitpackingFormatHelpers.GetIntegerBitPackedFormat(field);
                        bool formatSigned = formatInfo.Signed;
                        int formatBits = formatInfo.Bits;

                        // Determine type bits and helper method name
                        int integerTypeBits;
                        string helperMethodName;
                        bool isSignedType;

                        switch (typeName)
                        {
                            case "System.Byte":
                                helperMethodName = "ReadIntegerHelperByte";
                                integerTypeBits = 8;
                                isSignedType = false;
                                break;
                            case "System.SByte":
                                helperMethodName = "ReadIntegerHelperSByte";
                                integerTypeBits = 8;
                                isSignedType = true;
                                break;
                            case "System.UInt16":
                                helperMethodName = "ReadIntegerHelperUShort";
                                integerTypeBits = 16;
                                isSignedType = false;
                                break;
                            case "System.Int16":
                                helperMethodName = "ReadIntegerHelperShort";
                                integerTypeBits = 16;
                                isSignedType = true;
                                break;
                            case "System.UInt32":
                                helperMethodName = "ReadIntegerHelperUInt";
                                integerTypeBits = 32;
                                isSignedType = false;
                                break;
                            case "System.Int32":
                                helperMethodName = "ReadIntegerHelperInt";
                                integerTypeBits = 32;
                                isSignedType = true;
                                break;
                            case "System.UInt64":
                                helperMethodName = "ReadIntegerHelperULong";
                                integerTypeBits = 64;
                                isSignedType = false;
                                break;
                            case "System.Int64":
                                helperMethodName = "ReadIntegerHelperLong";
                                integerTypeBits = 64;
                                isSignedType = true;
                                break;
                            default:
                                throw new ArgumentException($"Unknown integer type: {typeName}");
                        }

                        // Resolve the helper method
                        MethodReference readIntegerHelperRef = Resolvers.ResolveMethod(
                            bitpackingHelpersType, assembly, Log, helperMethodName, ref WeavingFailed);

                        weaverBitCounter += formatBits;

                        // Load the struct (address for value types, instance for reference types)
                        worker.Emit(variable.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, 0);

                        // Call: BitpackingHelpers.ReadIntegerHelper[Type](reader, formatSigned, formatBits, typeBits, ref bitOffset, ref currentByte)
                        worker.Emit(OpCodes.Ldarg_0);  // Load reader
                        worker.Emit((formatSigned && isSignedType) ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); // Load formatSigned
                        worker.Emit(OpCodes.Ldc_I4, formatBits); // Load formatBits  
                        worker.Emit(OpCodes.Ldc_I4, integerTypeBits); // Load typeBits
                        worker.Emit(OpCodes.Ldloca, bitOffsetVarIndex);    // Load address of bitOffset
                        worker.Emit(OpCodes.Ldloca, currentByteVarIndex);  // Load address of currentByte
                        worker.Emit(OpCodes.Call, readIntegerHelperRef);

                        // Store result in field
                        worker.Emit(OpCodes.Stfld, assembly.MainModule.ImportReference(field));
                        break;


                    case "System.Single":
                    case "System.Double":
                        string readHelperMethodName;
                        BitpackingHelpers.DecimalFormatInfo decimalFormat;
                        if (typeName == "System.Single")
                        {
                            readHelperMethodName = "ReadFloatHelper";
                            decimalFormat = BitpackingFormatHelpers.GetFloatFormatInfo(field, Log);
                        }
                        else // if(typeName == "System.Double")
                        {
                            readHelperMethodName = "ReadDoubleHelper";
                            decimalFormat = BitpackingFormatHelpers.GetDoubleFormatInfo(field, Log);
                        }

                        weaverBitCounter += decimalFormat.ExponentBits + decimalFormat.MantissaBits + (decimalFormat.Signed ? 1 : 0);

                        // Resolve the helper method
                        MethodReference readHelperRef = Resolvers.ResolveMethod(
                            bitpackingHelpersType, assembly, Log, readHelperMethodName, ref WeavingFailed);

                        // Load the struct (address for value types, instance for reference types)
                        worker.Emit(variable.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc, 0);

                        // Call: BitpackingHelpers.ReadXXXHelper(reader, formatSigned, exponentBits, biasExponent, mantissaBits, ref bitOffset, ref currentByte)
                        worker.Emit(OpCodes.Ldarg_0);                                 // Load reader
                        worker.Emit(decimalFormat.Signed ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); // Load formatSigned
                        worker.Emit(OpCodes.Ldc_I4, decimalFormat.ExponentBits);      // Load exponentBits
                        worker.Emit(OpCodes.Ldc_I4, decimalFormat.NewBias);        // Load biasOffset
                        worker.Emit(OpCodes.Ldc_I4, decimalFormat.MantissaBits);      // Load mantissaBits
                        worker.Emit(OpCodes.Ldloca, bitOffsetVarIndex);               // Load address of bitOffset
                        worker.Emit(OpCodes.Ldloca, currentByteVarIndex);             // Load address of currentByte
                        worker.Emit(OpCodes.Call, readHelperRef);                     // Call helper

                        // Store result in field
                        worker.Emit(OpCodes.Stfld, assembly.MainModule.ImportReference(field));
                        break;

                    default:
                        WeavingFailed = true;
                        throw new NotSupportedException($"Field type '{typeName}' is not currently supported for bit-packing deserialization");
                }
            }

            return true;

        }

        void GenerateNullCheck(ILProcessor worker, ref bool WeavingFailed)
        {
            // if (!reader.ReadBoolean()) {
            //   return null;
            // }
            worker.Emit(OpCodes.Ldarg_0);
            worker.Emit(OpCodes.Call, GetReadFunc(weaverTypes.Import<bool>(), ref WeavingFailed));

            Instruction labelEmptyArray = worker.Create(OpCodes.Nop);
            worker.Emit(OpCodes.Brtrue, labelEmptyArray);
            // return null
            worker.Emit(OpCodes.Ldnull);
            worker.Emit(OpCodes.Ret);
            worker.Append(labelEmptyArray);
        }

        // Initialize the local variable with a new instance
        void CreateNew(TypeReference variable, ILProcessor worker, TypeDefinition td, ref bool WeavingFailed)
        {
            if (variable.IsValueType)
            {
                // structs are created with Initobj
                worker.Emit(OpCodes.Ldloca, 0);
                worker.Emit(OpCodes.Initobj, variable);
            }
            else if (td.IsDerivedFrom<UnityEngine.ScriptableObject>())
            {
                GenericInstanceMethod genericInstanceMethod = new GenericInstanceMethod(weaverTypes.ScriptableObjectCreateInstanceMethod);
                genericInstanceMethod.GenericArguments.Add(variable);
                worker.Emit(OpCodes.Call, genericInstanceMethod);
                worker.Emit(OpCodes.Stloc_0);
            }
            else
            {
                // classes are created with their constructor
                MethodDefinition ctor = Resolvers.ResolveDefaultPublicCtor(variable);
                if (ctor == null)
                {
                    Log.Error($"{variable.Name} can't be deserialized because it has no default constructor. Don't use {variable.Name} in [SyncVar]s, Rpcs, Cmds, etc.", variable);
                    WeavingFailed = true;
                    return;
                }

                MethodReference ctorRef = assembly.MainModule.ImportReference(ctor);

                worker.Emit(OpCodes.Newobj, ctorRef);
                worker.Emit(OpCodes.Stloc_0);
            }
        }

        void ReadAllFields(TypeReference variable, ILProcessor worker, ref bool WeavingFailed)
        {
            foreach (FieldDefinition field in variable.FindAllPublicFields())
            {
                // mismatched ldloca/ldloc for struct/class combinations is invalid IL, which causes crash at runtime
                OpCode opcode = variable.IsValueType ? OpCodes.Ldloca : OpCodes.Ldloc;
                worker.Emit(opcode, 0);
                MethodReference readFunc = GetReadFunc(field.FieldType, ref WeavingFailed);
                if (readFunc != null)
                {
                    worker.Emit(OpCodes.Ldarg_0);
                    worker.Emit(OpCodes.Call, readFunc);
                }
                else
                {
                    Log.Error($"{field.Name} has an unsupported type", field);
                    WeavingFailed = true;
                }
                FieldReference fieldRef = assembly.MainModule.ImportReference(field);

                worker.Emit(OpCodes.Stfld, fieldRef);
            }
        }

        // Save a delegate for each one of the readers into Reader<T>.read
        internal void InitializeReaders(ILProcessor worker)
        {
            ModuleDefinition module = assembly.MainModule;

            TypeReference genericReaderClassRef = module.ImportReference(typeof(Reader<>));

            System.Reflection.FieldInfo fieldInfo = typeof(Reader<>).GetField(nameof(Reader<object>.read));
            FieldReference fieldRef = module.ImportReference(fieldInfo);
            TypeReference networkReaderRef = module.ImportReference(typeof(NetworkReader));
            TypeReference funcRef = module.ImportReference(typeof(Func<,>));
            MethodReference funcConstructorRef = module.ImportReference(typeof(Func<,>).GetConstructors()[0]);

            foreach (KeyValuePair<TypeReference, MethodReference> kvp in readFuncs)
            {
                TypeReference targetType = kvp.Key;
                MethodReference readFunc = kvp.Value;

                // create a Func<NetworkReader, T> delegate
                worker.Emit(OpCodes.Ldnull);
                worker.Emit(OpCodes.Ldftn, readFunc);
                GenericInstanceType funcGenericInstance = funcRef.MakeGenericInstanceType(networkReaderRef, targetType);
                MethodReference funcConstructorInstance = funcConstructorRef.MakeHostInstanceGeneric(assembly.MainModule, funcGenericInstance);
                worker.Emit(OpCodes.Newobj, funcConstructorInstance);

                // save it in Reader<T>.read
                GenericInstanceType genericInstance = genericReaderClassRef.MakeGenericInstanceType(targetType);
                FieldReference specializedField = fieldRef.SpecializeField(assembly.MainModule, genericInstance);
                worker.Emit(OpCodes.Stsfld, specializedField);
            }
        }
    }
}
