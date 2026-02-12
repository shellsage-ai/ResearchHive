# Model Strategy (Free-first)

## Roles
- embeddings
- optional reranking
- synthesis/planning

## Defaults
- Local Ollama for embeddings + synthesis
- Paid provider optional and off by default; routed only for hard synthesis or final polish if user enables

## Rule
Use deterministic tooling for extraction wherever possible; LLMs for judgment/synthesis.
