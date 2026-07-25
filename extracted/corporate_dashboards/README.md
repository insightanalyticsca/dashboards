# corporate_dashboards — ASP.NET Core MVC + Local Ollama (Llama param) + RAG

## Prereqs
- .NET 8 SDK
- Ollama running locally (default: http://localhost:11434)
- Pull models (examples):
  - Chat model: llama3.1
  - Embedding model: nomic-embed-text

## Run
- `dotnet restore`
- `dotnet run --project corporate_dashboards/corporate_dashboards.csproj`

## Usage
1) Documents → Upload PDF/DOCX/TXT
2) Ask → enter question + (optional) override:
   - Chat model (Llama)
   - Embedding model

## Notes
- PDF text extraction uses PdfPig (text PDFs only; scanned PDFs need OCR).
- Embeddings stored in SQLite as JSON float arrays.
