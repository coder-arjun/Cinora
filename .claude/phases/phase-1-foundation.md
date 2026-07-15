# Phase 1 — Foundation

## Goal
A running, empty-but-solid application skeleton: Clean Architecture solution, database, authentication, design system, and quality guardrails — everything later phases build on.

## Prerequisites
None (first phase). Specs read; open inconsistencies (e.g., in-app chat scope) flagged to the user.

## In Scope
- Solution scaffold: `Cinora.Domain`, `Cinora.Application`, `Cinora.Infrastructure`, `Cinora.Web`, plus `Cinora.Domain.Tests`, `Cinora.Application.Tests`, `Cinora.Web.IntegrationTests`.
- Domain entities and EF Core model for: User, Friend, Movie, Genre, MovieGenre, Review, ReviewLike, Comment, Watchlist, Notification, Device, AIRecommendationHistory. Initial migration + seed data (genres).
- ASP.NET Identity + Google OAuth sign-in; Landing and Login pages.
- MediatR + FluentValidation pipeline (validation behavior), global exception handler with ProblemDetails, Serilog structured logging.
- Options Pattern settings classes for TMDB, OpenAI, Redis, Blob Storage (secrets via user-secrets).
- Tailwind CSS v4 pipeline + TypeScript build (esbuild) into `wwwroot`; base `_Layout` with design tokens (dark luxury theme), navigation shell, responsive grid.
- Local dev environment: SQL Server + Redis (containers or local), build/run scripts.

## Out of Scope
TMDB integration, any feature pages beyond Landing/Login/empty Home shell.

## Agents
architecture-agent (solution design first), database-agent, backend-agent, security-agent (Identity/OAuth), frontend-agent, devops-agent (local env), testing-agent, documentation-agent.

## Skills
clean-architecture-dotnet, ef-core-data-access, ef-core-migrations, cqrs-mediatr, fluent-validation, dotnet-configuration-options, error-handling-logging, aspnet-identity-google-oauth, tailwind-v4, typescript-frontend, razor-views, premium-ui-design.

## Review Gates
/review-architecture, /review-code, /review-security, /review-ui (layout shell). Performance loop optional this phase.

## Exit Criteria
- `dotnet build` and all tests pass.
- A user can register/sign in with email and with Google, and sees the authenticated Home shell.
- Initial migration creates the full schema on a clean SQL Server database.
- Tailwind/TS builds produce hashed, minified assets; base layout is responsive with the dark theme tokens.
- Zero Critical/High findings open from the review gates.
