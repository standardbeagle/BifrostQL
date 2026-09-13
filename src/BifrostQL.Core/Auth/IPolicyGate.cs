namespace BifrostQL.Core.Auth;

/// <summary>Checks table and column policy for an application endpoint.</summary>
public interface IPolicyGate
{
    PolicyDecision CanAct(string qualifiedTable, PolicyAction action);
    PolicyDecision CanWriteColumn(string qualifiedTable, string column);
    PolicyDecision CanReadColumn(string qualifiedTable, string column);
    void Require(string qualifiedTable, PolicyAction action);
}
