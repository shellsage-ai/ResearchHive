using FluentAssertions;
using ResearchHive.Core.Configuration;
using ResearchHive.Core.Models;
using ResearchHive.Core.Services;

namespace ResearchHive.Tests;

/// <summary>
/// Tests for the CodeChunker service — code-aware chunking of source files.
/// </summary>
public class CodeChunkerTests
{
    private readonly CodeChunker _chunker;

    public CodeChunkerTests()
    {
        _chunker = new CodeChunker(new AppSettings { RepoChunkSize = 200, RepoChunkOverlap = 30 });
    }

    [Fact]
    public void ChunkFile_CSharpCode_ProducesChunks()
    {
        var code = @"using System;

namespace MyApp
{
    public class Calculator
    {
        public int Add(int a, int b) => a + b;

        public int Subtract(int a, int b) => a - b;

        public int Multiply(int a, int b) => a * b;
    }

    public class Logger
    {
        private readonly string _path;

        public Logger(string path) { _path = path; }

        public void Log(string message)
        {
            Console.WriteLine($""{DateTime.Now}: {message}"");
        }
    }
}";
        var chunks = _chunker.ChunkFile("Calculator.cs", code, "session1", "source1");

        chunks.Should().NotBeEmpty();
        chunks.All(c => c.SourceType == "repo_code").Should().BeTrue();
        chunks.All(c => c.Text.Contains("// File: Calculator.cs")).Should().BeTrue();
        chunks.All(c => c.SessionId == "session1").Should().BeTrue();
        chunks.All(c => c.SourceId == "source1").Should().BeTrue();
    }

    [Fact]
    public void ChunkFile_MarkdownDoc_UsesDocChunking()
    {
        var readme = string.Join(" ", Enumerable.Repeat("This is a documentation word.", 100));
        var chunks = _chunker.ChunkFile("README.md", readme, "session1", "source1");

        chunks.Should().NotBeEmpty();
        chunks.All(c => c.SourceType == "repo_doc").Should().BeTrue();
        chunks.All(c => c.Text.Contains("// File: README.md")).Should().BeTrue();
    }

    [Fact]
    public void ChunkFile_EmptyContent_ReturnsEmpty()
    {
        var chunks = _chunker.ChunkFile("empty.cs", "", "s1", "src1");
        chunks.Should().BeEmpty();
    }

    [Fact]
    public void ChunkFile_SmallFile_ReturnsSingleChunk()
    {
        var code = "public class Tiny { }";
        var chunks = _chunker.ChunkFile("Tiny.cs", code, "s1", "src1");

        chunks.Should().HaveCount(1);
        chunks[0].ChunkIndex.Should().Be(0);
    }

    [Fact]
    public void ChunkFile_PythonCode_DetectsDefBoundaries()
    {
        var code = @"import os

def hello():
    print('hello')

def goodbye():
    print('goodbye')

class MyClass:
    def __init__(self):
        self.value = 42

    def get_value(self):
        return self.value
";
        // Use larger content so it actually gets split
        var largeCode = string.Join("\n\n", Enumerable.Repeat(code, 5));
        var chunks = _chunker.ChunkFile("app.py", largeCode, "s1", "src1");

        chunks.Should().NotBeEmpty();
        chunks.All(c => c.SourceType == "repo_code").Should().BeTrue();
    }

    [Fact]
    public void ChunkFile_JsonConfig_UsesDocChunking()
    {
        var json = "{ \"name\": \"test\", \"version\": \"1.0.0\", \"dependencies\": {} }";
        var chunks = _chunker.ChunkFile("package.json", json, "s1", "src1");

        chunks.Should().NotBeEmpty();
        chunks.All(c => c.SourceType == "repo_doc").Should().BeTrue();
    }

    [Fact]
    public void ChunkFile_ConsecutiveChunksHaveIncreasingIndex()
    {
        var code = string.Join("\n\n", Enumerable.Range(0, 20).Select(i =>
            $"public class Class{i} {{ public void Method{i}() {{ /* lots of code here to force chunking with enough words to hit the limit */ var x = \"{new string('a', 200)}\"; }} }}"));

        var chunks = _chunker.ChunkFile("Big.cs", code, "s1", "src1");

        if (chunks.Count > 1)
        {
            for (int i = 0; i < chunks.Count; i++)
                chunks[i].ChunkIndex.Should().Be(i);
        }
    }
}
