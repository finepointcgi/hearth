using System.Text.Json.Nodes;
using Locus.Core.Services;

namespace Locus.Core.Tools;

public sealed class ImageGenTool(ImageGenService images) : ITool
{
    public string Name => "generate_image";
    public string DisplayName => "Generate image";
    public string Icon => "🎨";
    public string Description =>
        "Create an image with the user's local Stable Diffusion server. Write a detailed visual prompt in English " +
        "(subject, style, lighting, composition). The image is shown to the user automatically — don't describe it as a link.";
    public JsonObject Parameters => Schema.Object(
        ("prompt", "string", "Detailed description of the image", true),
        ("negative_prompt", "string", "Things to avoid", false),
        ("width", "integer", "Pixels, multiple of 64 (default from settings)", false),
        ("height", "integer", "Pixels, multiple of 64 (default from settings)", false));
    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var prompt = args.Str("prompt");
        if (string.IsNullOrWhiteSpace(prompt)) return ToolResult.Error("prompt is required");
        var img = await images.GenerateAsync(ctx.Settings.ImageGen, prompt, args.Str("negative_prompt"),
            (int?)args.Num("width"), (int?)args.Num("height"), ct);
        return new ToolResult("Image generated and displayed to the user.", Images: [img]);
    }
}
