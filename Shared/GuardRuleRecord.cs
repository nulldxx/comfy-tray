using System;

namespace ComfyTray;

/// <summary>
/// One firewall rule owned by the guard, in terms the pure code can reason about. The COM
/// layer converts to and from <c>INetFwRule</c>; nothing above it needs to know that
/// Windows Firewall exists.
/// </summary>
/// <param name="RuleName">The rule's <c>Name</c>, from <see cref="GuardRuleNaming.Compose"/>.</param>
/// <param name="SessionId">The session that owns it.</param>
/// <param name="ImagePath">The normalised executable path the rule blocks.</param>
internal sealed record GuardRuleRecord(string RuleName, Guid SessionId, string ImagePath);
