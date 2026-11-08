using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Hearth.Core.Llm;
using Hearth.Core.Models;

namespace Hearth.Core.Services;

/// <summary>Local image generation via AUTOMATIC1111/Forge (/sdapi) or ComfyUI (/prompt).</summary>
public sealed class ImageGenService(IHttpClientFactory http)
{
    private HttpClient Client => http.CreateClient(LlmClientFactory.HttpClientName);

    public async Task<List<string>> ListModelsAsync(ImageGenSettings s, CancellationToken ct)
    {
        var url = s.Url.TrimEnd('/');
        if (s.Backend == ImageBackend.Automatic1111)
        {
            var arr = await Client.GetFromJsonAsync<JsonArray>($"{url}/sdapi/v1/sd-models", ct);
            return arr?.Select(m => m?["title"]?.ToString() ?? "").Where(t => t.Length > 0).ToList() ?? [];
        }
        if (s.Backend == ImageBackend.ComfyUI)
        {
            var info = await Client.GetFromJsonAsync<JsonObject>($"{url}/object_info/CheckpointLoaderSimple", ct);
            return info?["CheckpointLoaderSimple"]?["input"]?["required"]?["ckpt_name"]?[0]?.AsArray()
                .Select(n => n?.ToString() ?? "").ToList() ?? [];
        }
        return [];
    }

    public async Task<ImageAttachment> GenerateAsync(ImageGenSettings s, string prompt, string? negative,
        int? width, int? height, CancellationToken ct)
    {
        var w = Snap(width ?? s.Width);
        var h = Snap(height ?? s.Height);
        var neg = string.IsNullOrWhiteSpace(negative) ? s.NegativePrompt : negative;
        return s.Backend switch
        {
            ImageBackend.Automatic1111 => await A1111Async(s, prompt, neg, w, h, ct),
            ImageBackend.ComfyUI => await ComfyAsync(s, prompt, neg, w, h, ct),
            _ => throw new InvalidOperationException("No image backend configured."),
        };
    }

    private static int Snap(int v) => Math.Clamp(v / 64 * 64, 256, 2048);

    private async Task<ImageAttachment> A1111Async(ImageGenSettings s, string prompt, string neg, int w, int h, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["prompt"] = prompt,
            ["negative_prompt"] = neg,
            ["steps"] = s.Steps,
            ["cfg_scale"] = s.CfgScale,
            ["width"] = w,
            ["height"] = h,
        };
        if (!string.IsNullOrWhiteSpace(s.Model))
            payload["override_settings"] = new JsonObject { ["sd_model_checkpoint"] = s.Model };
        using var res = await Client.PostAsJsonAsync($"{s.Url.TrimEnd('/')}/sdapi/v1/txt2img", payload, ct);
        var body = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(body?["error"]?.ToString() ?? res.ReasonPhrase);
        var b64 = body?["images"]?[0]?.ToString() ?? throw new InvalidOperationException("No image returned.");
        return new ImageAttachment(Slug(prompt) + ".png", "image/png", b64);
    }

    private async Task<ImageAttachment> ComfyAsync(ImageGenSettings s, string prompt, string neg, int w, int h, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Model))
            throw new InvalidOperationException("Set a checkpoint name for ComfyUI in Settings → Image generation.");
        var url = s.Url.TrimEnd('/');
        // Standard text-to-image graph.
        var graph = JsonNode.Parse("""
        {
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":""}},
          "5": {"class_type":"EmptyLatentImage","inputs":{"width":512,"height":512,"batch_size":1}},
          "6": {"class_type":"CLIPTextEncode","inputs":{"text":"","clip":["4",1]}},
          "7": {"class_type":"CLIPTextEncode","inputs":{"text":"","clip":["4",1]}},
          "3": {"class_type":"KSampler","inputs":{"seed":0,"steps":20,"cfg":7,
                "sampler_name":"euler","scheduler":"normal","denoise":1,
                "model":["4",0],"positive":["6",0],"negative":["7",0],"latent_image":["5",0]}},
          "8": {"class_type":"VAEDecode","inputs":{"samples":["3",0],"vae":["4",2]}},
          "9": {"class_type":"SaveImage","inputs":{"filename_prefix":"hearth","images":["8",0]}}
        }
        """)!;
        graph["4"]!["inputs"]!["ckpt_name"] = s.Model;
        graph["5"]!["inputs"]!["width"] = w;
        graph["5"]!["inputs"]!["height"] = h;
        graph["3"]!["inputs"]!["seed"] = Random.Shared.NextInt64(1, 1L << 48);
        graph["3"]!["inputs"]!["steps"] = s.Steps;
        graph["3"]!["inputs"]!["cfg"] = s.CfgScale;
        graph["6"]!["inputs"]!["text"] = prompt;
        graph["7"]!["inputs"]!["text"] = neg;

        using var res = await Client.PostAsJsonAsync($"{url}/prompt",
            new JsonObject { ["prompt"] = graph, ["client_id"] = "hearth" }, ct);
        var body = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(body?["error"]?["message"]?.ToString() ?? body?.ToJsonString() ?? res.ReasonPhrase);
        var id = body?["prompt_id"]?.ToString() ?? throw new InvalidOperationException("ComfyUI didn't return a prompt id.");

        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, ct);
            var hist = await Client.GetFromJsonAsync<JsonObject>($"{url}/history/{id}", ct);
            var outputs = hist?[id]?["outputs"]?.AsObject();
            var img = outputs?.Select(o => o.Value?["images"]?[0]).FirstOrDefault(i => i is not null);
            if (img is null) continue;
            var q = $"filename={Uri.EscapeDataString(img["filename"]!.ToString())}&subfolder={Uri.EscapeDataString(img["subfolder"]?.ToString() ?? "")}&type={img["type"]}";
            var bytes = await Client.GetByteArrayAsync($"{url}/view?{q}", ct);
            return new ImageAttachment(Slug(prompt) + ".png", "image/png", Convert.ToBase64String(bytes));
        }
        throw new TimeoutException("ComfyUI didn't finish within 10 minutes.");
    }

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').Take(40).ToArray()).Trim('-');
}
