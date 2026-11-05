using Locus.Core.Models;

namespace Locus.UI.State;

public sealed record AttachedDoc(string Name, string? Text, bool Indexed);

/// <param name="UntrustedSource">Set when attached content came from the web (@https://…): the run is guarded.</param>
public sealed record ComposerInput(string Text, List<ImageAttachment> Images, List<AttachedDoc> Docs, string? UntrustedSource = null);
