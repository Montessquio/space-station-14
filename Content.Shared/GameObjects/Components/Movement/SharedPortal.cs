using System;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;

namespace Content.Shared.GameObjects.Components.Movement
{
    public abstract class SharedPortalComponent : Component {
        public override string Name => "Portal";
    }

    [Serializable, NetSerializable]
    public enum PortalVisuals
    {
        State
    }

    [Serializable, NetSerializable]
    public enum PortalState
    {
        RecentlyTeleported,
        Pending,
        UnableToTeleport,
    }

}
