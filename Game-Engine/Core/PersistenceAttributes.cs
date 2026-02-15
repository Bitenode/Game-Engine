using System;

namespace Game_Engine.Core
{
    /// <summary>Mark a property/field to be persisted even if it would normally be skipped.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class PersistAttribute : Attribute { }

    /// <summary>Mark a property/field to NOT be persisted by the generic serializer.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class DoNotPersistAttribute : Attribute { }
}
