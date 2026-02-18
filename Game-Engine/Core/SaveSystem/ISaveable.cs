#nullable enable
using System.Collections.Generic;

namespace Game_Engine.Core.SaveSystem
{
    /// <summary>
    /// Interface for components that support runtime game state persistence.
    /// Implement on Behaviors that need to save/load data beyond scene serialization.
    /// </summary>
    public interface ISaveable
    {
        /// <summary>Unique identifier for this saveable instance. Usually the GameObject name + component type.</summary>
        string SaveId { get; }

        /// <summary>Write save data to the dictionary.</summary>
        void OnSave(Dictionary<string, object> data);

        /// <summary>Read save data from the dictionary.</summary>
        void OnLoad(Dictionary<string, object> data);
    }
}
