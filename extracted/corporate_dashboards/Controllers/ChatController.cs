using corporate_dashboards.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace corporate_dashboards.Controllers;

public sealed class ChatController : Controller
{
    private readonly IRetrievalService _rag;
    private readonly OllamaOptions _ollama;

    public ChatController(IRetrievalService rag, IOptions<OllamaOptions> ollama)
    {
        _rag = rag;
        _ollama = ollama.Value;
    }

    [HttpGet]
    public IActionResult Index()
    {
        ViewBag.DefaultChatModel = _ollama.ChatModel;
        ViewBag.DefaultEmbeddingModel = _ollama.EmbeddingModel;
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Ask(string question, string? chatModel, string? embeddingModel, CancellationToken ct)
    {
        question = (question ?? "").Trim();
        if (string.IsNullOrWhiteSpace(question)) return RedirectToAction(nameof(Index));

        var answer = await _rag.AnswerAsync(question, chatModel, embeddingModel, ct);
        var sources = await _rag.RetrieveAsync(question, embeddingModel, ct);

        ViewBag.Question = question;
        ViewBag.Answer = answer;
        ViewBag.Sources = sources;
        ViewBag.ChatModel = string.IsNullOrWhiteSpace(chatModel) ? _ollama.ChatModel : chatModel;
        ViewBag.EmbeddingModel = string.IsNullOrWhiteSpace(embeddingModel) ? _ollama.EmbeddingModel : embeddingModel;

        return View("Result");
    }
}
