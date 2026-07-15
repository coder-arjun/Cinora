---
name: xunit-testing
description: Use when writing or reviewing Cinora unit tests — new xUnit test classes, MediatR handler tests, FluentValidation validator tests, NSubstitute mocking, EF provider choices for tests, or test naming and structure questions.
---

# xUnit Testing

## Overview
One test project per layer (`Cinora.Domain.Tests`, `Cinora.Application.Tests`), tests named `Method_Scenario_ExpectedOutcome`, strict AAA structure — each test proves exactly one behavior with a failure message a stranger can read.

## Quick Reference
| Task | Approach |
|---|---|
| Naming | `Handle_RatingOutOfRange_ReturnsValidationError` |
| Structure | Arrange / Act / Assert; one behavior per test |
| Assertions | FluentAssertions — `result.Should().BeTrue()` over `Assert.True` |
| Test doubles | NSubstitute on your own ports/interfaces only |
| MediatR handlers | Mock ports; SQLite in-memory only for trivial queries — prefer real SQL in integration tests |
| Validators | FluentValidation `TestValidate` + `ShouldHaveValidationErrorFor` |
| Boundaries | `[Theory]`/`[InlineData]` covering 0, 1, 10, 11 for the 1–10 rating scale |

## Pattern
```csharp
public class CreateReviewHandlerTests
{
    private readonly IReviewRepository _reviews = Substitute.For<IReviewRepository>();
    private readonly CreateReviewHandler _sut;

    public CreateReviewHandlerTests() => _sut = new CreateReviewHandler(_reviews);

    [Theory]
    [InlineData(1)]  // WHY: lower boundary of the 1-10 rating scale
    [InlineData(10)] // WHY: upper boundary — boundaries are where bugs live
    public async Task Handle_ValidRating_PersistsReview(int rating)
    {
        // Arrange
        var command = new CreateReviewCommand(MovieId: 42, Rating: rating, Body: "Stunning.");

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert — FluentAssertions gives descriptive failure output
        result.IsSuccess.Should().BeTrue();
        // WHY: verify the observable outcome (persistence call with correct data),
        // not internal implementation details
        await _reviews.Received(1).AddAsync(
            Arg.Is<Review>(r => r.Rating == rating),
            Arg.Any<CancellationToken>());
    }
}
```

Validator tests belong beside the validators they cover — see `.claude/skills/fluent-validation/SKILL.md` for `TestValidate` usage. Handler shape and pipeline behaviors are defined in `.claude/skills/cqrs-mediatr/SKILL.md`. Anything touching the real database, MVC pipeline, or auth belongs in `.claude/skills/integration-testing/SKILL.md`.

## Common Mistakes
| Mistake | Fix |
|---|---|
| EF InMemory provider as proof of correctness | It ignores relational rules; unit-test via ports, verify SQL behavior in integration tests |
| Mocking types you don't own (`DbContext`, `HttpClient`) | Wrap in a port interface and mock that |
| Several asserts on unrelated behaviors | Split into one test per behavior |
| `Theory` data without boundary values | Always include just-inside and just-outside values (0, 1, 10, 11) |
| Names like `Test1` or `HandleWorks` | `Method_Scenario_ExpectedOutcome` — the name is documentation |
| Shared mutable state between tests | xUnit creates a new class instance per test; keep setup in the constructor |
