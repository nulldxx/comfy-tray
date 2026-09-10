using System;
using System.Runtime.InteropServices;

namespace ComfyTray;

// Windows Firewall COM declarations.
//
// The IIDs below come from netfw.idl in the Windows SDK, and the member names from the netfw.h
// reference documentation. Both matter and neither can be guessed: a wrong IID fails at runtime
// with E_NOINTERFACE, and a wrong member name with DISP_E_UNKNOWNNAME. Do not edit them without
// checking the header.
//
// Every interface is declared InterfaceIsIDispatch rather than the more usual InterfaceIsDual.
// That is a deliberate trade. A dual declaration is dispatched through the vtable, so the
// *order* of the members has to match the IDL exactly and getting it wrong calls the wrong
// function with the wrong arguments — silent memory corruption rather than a clean error. An
// IDispatch declaration is dispatched by name, so order is irrelevant and only the members we
// actually use need declaring. These interfaces are called a handful of times per session, so
// the cost of late binding is irrelevant next to the safety.

/// <summary>Action taken by a firewall rule. <c>NET_FW_ACTION</c>.</summary>
internal enum NetFwAction
{
    Block = 0,
    Allow = 1,
}

/// <summary>Traffic direction a rule applies to. <c>NET_FW_RULE_DIRECTION</c>.</summary>
internal enum NetFwRuleDirection
{
    In = 1,
    Out = 2,
}

/// <summary>Firewall profile. <c>NET_FW_PROFILE_TYPE2</c>.</summary>
[Flags]
internal enum NetFwProfileType2
{
    Domain = 0x1,
    Private = 0x2,
    Public = 0x4,

    /// <summary>All profiles, present and future. This is the literal the API defines, not a fabricated mask.</summary>
    All = 0x7FFFFFFF,
}

/// <summary>IP protocol selector. <c>NET_FW_IP_PROTOCOL</c>.</summary>
internal enum NetFwIpProtocol
{
    Any = 256,
}

/// <summary>
/// Whether a change to local firewall policy will actually take effect.
/// <c>NET_FW_MODIFY_STATE</c>.
/// </summary>
internal enum NetFwModifyState
{
    /// <summary>Local rules are honoured.</summary>
    Ok = 0,

    /// <summary>
    /// Group policy has taken over the profile, so rules created locally are stored but never
    /// evaluated. The guard reports this rather than pretending to enforce anything.
    /// </summary>
    GroupPolicyOverride = 1,

    /// <summary>All inbound traffic is blocked; irrelevant to outbound rules but part of the enum.</summary>
    InboundBlocked = 2,
}

/// <summary>A single firewall rule. <c>INetFwRule</c>.</summary>
[ComImport]
[Guid("AF230D27-BABA-4E42-ACED-F524F22CFCE2")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface INetFwRule
{
    /// <summary>Rule name. Windows Firewall rejects a name containing '|' or equal to "all".</summary>
    string Name { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }

    /// <summary>Free text shown in the rule's properties. Also may not contain '|'.</summary>
    string Description { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }

    /// <summary>Full path of the executable the rule applies to.</summary>
    string ApplicationName { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }

    /// <summary>IP protocol. Must be set before ports, per the API's ordering rules.</summary>
    int Protocol { get; set; }

    NetFwRuleDirection Direction { get; set; }

    bool Enabled { get; set; }

    /// <summary>
    /// The rule's group. Documented as taking an indirect string for localisation; a plain
    /// literal is accepted and is what netsh and the PowerShell cmdlets write.
    /// </summary>
    string Grouping { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }

    int Profiles { get; set; }

    NetFwAction Action { get; set; }
}

/// <summary>The machine's collection of firewall rules. <c>INetFwRules</c>.</summary>
[ComImport]
[Guid("9C4C6277-5027-441E-AFAE-CA1F542DA009")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface INetFwRules
{
    int Count { get; }

    void Add(INetFwRule rule);

    /// <summary>Removes every rule with this name. Throws when the name is not present.</summary>
    void Remove([MarshalAs(UnmanagedType.BStr)] string name);

    INetFwRule Item([MarshalAs(UnmanagedType.BStr)] string name);

    /// <summary>
    /// <c>_NewEnum</c>, the standard OLE Automation enumerator. Declared as a property with an
    /// explicit DISPID rather than by name: the member's real name begins with an underscore and
    /// is conventionally hidden, so name lookup is unreliable, whereas DISPID_NEWENUM (-4) is
    /// fixed by the automation specification. Property rather than method so that the call is
    /// dispatched as DISPATCH_PROPERTYGET, which is how MIDL declares it.
    /// </summary>
    [DispId(-4)]
    object NewEnum { [return: MarshalAs(UnmanagedType.IUnknown)] get; }
}

/// <summary>The firewall policy itself. <c>INetFwPolicy2</c>.</summary>
[ComImport]
[Guid("98325047-C671-4174-8D81-DEFCD3F03186")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface INetFwPolicy2
{
    /// <summary>Profiles currently in effect, as a bitmask of <see cref="NetFwProfileType2"/>.</summary>
    int CurrentProfileTypes { get; }

    INetFwRules Rules { get; }
}
