using Locus.Core.Models;

namespace Locus.UI.State;

public sealed record AttachedDoc(string Name, string? Text, bool Indexed);

public sealed record ComposerInput(string Text, List<ImageAttachment> Images, List<AttachedDoc> Docs);
