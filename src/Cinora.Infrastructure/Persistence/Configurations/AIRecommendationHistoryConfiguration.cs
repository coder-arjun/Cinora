using Cinora.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cinora.Infrastructure.Persistence.Configurations;

/// <summary>Maps the <see cref="AIRecommendationHistory"/> audit entity.</summary>
internal sealed class AIRecommendationHistoryConfiguration : IEntityTypeConfiguration<AIRecommendationHistory>
{
    public void Configure(EntityTypeBuilder<AIRecommendationHistory> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.Model)
               .IsRequired()
               .HasMaxLength(AIRecommendationHistory.ModelMaxLength);

        builder.Property(a => a.InputSummary)
               .IsRequired()
               .HasMaxLength(AIRecommendationHistory.SummaryMaxLength);

        builder.Property(a => a.OutputSummary)
               .IsRequired()
               .HasMaxLength(AIRecommendationHistory.SummaryMaxLength);

        builder.Property(a => a.PromptTokens).IsRequired();
        builder.Property(a => a.CompletionTokens).IsRequired();
        builder.Property(a => a.GeneratedAtUtc).IsRequired();

        // WHY: a user's most recent recommendation generations.
        builder.HasIndex(a => new { a.UserId, a.GeneratedAtUtc });

        // Restrict — deleting a user must not erase their recommendation audit trail.
        builder.HasOne<User>()
               .WithMany()
               .HasForeignKey(a => a.UserId)
               .OnDelete(DeleteBehavior.Restrict);
    }
}
