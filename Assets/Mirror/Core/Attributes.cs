using System;
using UnityEngine;

namespace Mirror
{


    /// <summary>
    /// Used to specify the maximum value of a field. For example, a normal int has 32 bits and supports values between [-2147483647, 2147483647]
    /// Using only 16 bits still supports values between [-65535, 65535]. Most values in your game will not require the full ranges of their types, so big reductions in bandwidth can be achieved.
    /// Used to determine the number of bits to use when serializing an integer field over the network.
    /// Reduces bandwidth by packing multiple small integer values into fewer bytes.
    /// Example: [BitPacked(5)] uses only 5 bits instead of 32 for values 0-31.
    /// Adjacent bit-packed fields are automatically grouped and packed together.
    /// Supports 1-32 bits per field. Only applicable to int fields.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class IntegerBitPackedAttribute : Attribute
    {
        public int BitCount { get; }

        public IntegerBitPackedAttribute(int bitCount)
        {
            if (bitCount < 1 || bitCount > 32)
                throw new ArgumentException("Bit count must be between 1 and 32");

            BitCount = bitCount;
        }
    }


    /// <summary>
    /// Used to specify bit packing parameters for decimal/floating point fields.
    /// Allows efficient network serialization by defining the range and precision requirements.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class DecimalBitPackedAttribute : Attribute
    {
        public bool Signed { get; }
        public float MaxValue { get; }
        public float MinPrecision { get; }

        public DecimalBitPackedAttribute(bool signed, float maxValue, float minPrecision)
        {
            if (maxValue <= 0)
                throw new ArgumentException("MaxValue must be greater than 0");

            if (minPrecision <= 0 || minPrecision >= 1)
                throw new ArgumentException("MinPrecision must be greater than 0 and less than 1");

            Signed = signed;
            MaxValue = maxValue;
            MinPrecision = minPrecision;
        }
    }

    /// <summary>
    /// SyncVars are used to automatically synchronize a variable between the server and all clients. The direction of synchronization depends on the Sync Direction property, ServerToClient by default.
    /// <para>
    /// When Sync Direction is equal to ServerToClient, the value should be changed on the server side and synchronized to all clients.
    /// Otherwise, the value should be changed on the client side and synchronized to server and other clients.
    /// </para>
    /// <para>Hook parameter allows you to define a method to be invoked when gets an value update. Notice that the hook method will not be called on the change side.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class SyncVarAttribute : PropertyAttribute
    {
        public string hook;
    }

    /// <summary>
    /// Call this from a client to run this function on the server.
    /// <para>Make sure to validate input etc. It's not possible to call this from a server.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class CommandAttribute : Attribute
    {
        public int channel = Channels.Reliable;
        public bool requiresAuthority = true;
    }

    /// <summary>
    /// The server uses a Remote Procedure Call (RPC) to run this function on clients.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class ClientRpcAttribute : Attribute
    {
        public int channel = Channels.Reliable;
        public bool includeOwner = true;
    }

    /// <summary>
    /// The server uses a Remote Procedure Call (RPC) to run this function on a specific client.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class TargetRpcAttribute : Attribute
    {
        public int channel = Channels.Reliable;
    }

    /// <summary>
    /// Only an active server will run this method.
    /// <para>Prints a warning if a client or in-active server tries to execute this method.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class ServerAttribute : Attribute {}

    /// <summary>
    /// Only an active server will run this method.
    /// <para>No warning is thrown.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class ServerCallbackAttribute : Attribute {}

    /// <summary>
    /// Only an active client will run this method.
    /// <para>Prints a warning if the server or in-active client tries to execute this method.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class ClientAttribute : Attribute {}

    /// <summary>
    /// Only an active client will run this method.
    /// <para>No warning is printed.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class ClientCallbackAttribute : Attribute {}

    /// <summary>
    /// Converts a string property into a Scene property in the inspector
    /// </summary>
    public class SceneAttribute : PropertyAttribute {}

    /// <summary>
    /// Used to show private SyncList in the inspector,
    /// <para> Use instead of SerializeField for non Serializable types </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class ShowInInspectorAttribute : Attribute {}

    /// <summary>
    /// Used to make a field readonly in the inspector
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class ReadOnlyAttribute : PropertyAttribute {}

    /// <summary>
    /// When defining multiple Readers/Writers for the same type, indicate which one Weaver must use.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class WeaverPriorityAttribute : Attribute {}
}
