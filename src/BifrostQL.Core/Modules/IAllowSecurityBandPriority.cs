namespace BifrostQL.Core.Modules;

/// <summary>
/// Opt-in marker for a consumer-supplied filter or mutation transformer that
/// deliberately runs in the reserved security band (priority below
/// <see cref="BifrostProfile.SecurityBandFloor"/>, i.e. 0-99).
///
/// That band is reserved for the host's built-in security transformers so a consumer
/// transformer cannot silently run <em>ahead</em> of tenant isolation, authorization
/// policy, or the state machine. <see cref="ModulePriorityFloorGuard"/> rejects any
/// consumer transformer below the floor at composition time; implementing this
/// interface is the explicit acknowledgement that ordering ahead of the built-in
/// security guards is intentional (e.g. a host-embedded transformer that must observe
/// the mutation before the policy engine gates it).
///
/// Built-in transformers shipped in the BifrostQL.Core assembly are exempt from the
/// guard and never need this marker.
/// </summary>
public interface IAllowSecurityBandPriority
{
}
