using System;
using System.Collections.Generic;
using Mono.CecilX;
using Mono.CecilX.Cil;
// to use Mono.CecilX.Rocks here, we need to 'override references' in the
// Unity.Mirror.CodeGen assembly definition file in the Editor, and add CecilX.Rocks.
// otherwise we get an unknown import exception.
using Mono.CecilX.Rocks;
using Mirror.Weaver;

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

            if (!variable.Resolve().IsValueType)
                WriteNullCheck(worker, ref WeavingFailed);

            if (!WriteAllFields(variable, worker, ref WeavingFailed))
                return null;

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

        // Find all fields in type and write them
        bool WriteAllFields(TypeReference variable, ILProcessor worker, ref bool WeavingFailed)
        {
            // Track bit-packed fields for batching
            List<(FieldDefinition field, int bitCount)> bitPackedFields = new List<(FieldDefinition, int)>();
            int currentBitOffset = 0;

            foreach (FieldDefinition field in variable.FindAllPublicFields())
            {
                // Get bit count - either from attribute or full size of type
                bool isDecimalType = false;

                if (isDecimalType)
                {
                    BitpackingHelpers.DecimalBitPackedInfo decimalInfo = BitpackingHelpers.GetDecimalBitPackInfo(field);
                    BitpackingHelpers.DecimalFormatInfo format = BitpackingHelpers.GetDecimalFormatInfo(decimalInfo);

                }
                else
                {
                    int bitCount = BitpackingHelpers.GetIntegerBitPackedCount(field);
                    if (bitCount <= 0)
                    {
                        bitCount = GetTypeSizeInBits(field.FieldType);
                    }

                    // Add to bit-packed collection
                    bitPackedFields.Add((field, bitCount));
                    currentBitOffset += bitCount;

                    // If we've accumulated enough bits, write the batch
                    // Using 32 bits as the batch size for now
                    if (currentBitOffset >= 32)
                    {
                        WriteBitPackedBatch(worker, bitPackedFields, ref WeavingFailed);
                        bitPackedFields.Clear();
                        currentBitOffset = 0;
                    }
                }
            }

            // Flush any remaining bit-packed fields
            if (bitPackedFields.Count > 0)
            {
                WriteBitPackedBatch(worker, bitPackedFields, ref WeavingFailed);
            }

            return true;
        }


       
        // TODO MAKE THIS GOOD
        void WriteBitPackedBatch(ILProcessor worker, List<(FieldDefinition field, int bitCount)> fields, ref bool WeavingFailed)
        {
            // Local variable to accumulate bits
            worker.Emit(OpCodes.Ldc_I4_0); // uint packed = 0
            worker.Emit(OpCodes.Stloc_0);

            int bitOffset = 0;

            foreach (var (field, bitCount) in fields)
            {
                FieldReference fieldRef = assembly.MainModule.ImportReference(field);

                // Load the field value
                worker.Emit(OpCodes.Ldarg_1); // load 'value' parameter
                worker.Emit(OpCodes.Ldfld, fieldRef);

                // Create bit mask for the field
                uint mask = (uint)((1 << bitCount) - 1);

                // Apply mask to ensure we only use specified bits
                worker.Emit(OpCodes.Ldc_I4, (int)mask);
                worker.Emit(OpCodes.And);

                // Shift left by current bit offset
                if (bitOffset > 0)
                {
                    worker.Emit(OpCodes.Ldc_I4, bitOffset);
                    worker.Emit(OpCodes.Shl);
                }

                // OR with accumulated value
                worker.Emit(OpCodes.Ldloc_0);
                worker.Emit(OpCodes.Or);
                worker.Emit(OpCodes.Stloc_0);

                bitOffset += bitCount;
            }

            // Write the packed uint
            worker.Emit(OpCodes.Ldarg_0); // writer
            worker.Emit(OpCodes.Ldloc_0); // packed value

            MethodReference writeUInt = GetWriteFunc(weaverTypes.Import<uint>(), ref WeavingFailed);
            worker.Emit(OpCodes.Call, writeUInt);
        }

        // TODO: Surely there is something better we can do here no? 
        int GetTypeSizeInBits(TypeReference type)
        {
            // Handle primitive types
            switch (type.FullName)
            {
                case "System.Boolean":
                    return 8; // Or 1 if you want to pack bools tightly
                case "System.Byte":
                case "System.SByte":
                    return 8;
                case "System.Int16":
                case "System.UInt16":
                    return 16;
                case "System.Int32":
                case "System.UInt32":
                case "System.Single":
                    return 32;
                case "System.Int64":
                case "System.UInt64":
                case "System.Double":
                    return 64;
                default:
                    // For enums, get the underlying type size
                    if (type.Resolve()?.IsEnum ?? false)
                    {
                        return GetTypeSizeInBits(type.Resolve().GetEnumUnderlyingType());
                    }
                    // Default to 32 bits for unknown types (you might want to handle this differently)
                    Log.Warning($"Unknown type size for {type.FullName}, defaulting to 32 bits");
                    return 32;
            }
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
