# Chunkwise
Chunkwise is a .NET 8 lab showing how document chunking changes RAG retrieval. It compares 7 chunking strategies (fixed, sentence, paragraph, recursive, Markdown, semantic, parent-child) on a synthetic corpus with 30 golden questions, scoring Recall@1/3, MRR and coverage. Includes a zero-setup web UI, CLI, pluggable embedders and 251 tests.
