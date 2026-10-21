using MiRenta.Domain.Assistant;
using MiRenta.Domain.Cases;
using MiRenta.Domain.Documents;
using MiRenta.Domain.Engine;
using MiRenta.Domain.Facts;
using MiRenta.Domain.Finance;
using MiRenta.Domain.Identity;
using MiRenta.Domain.Operations;
using MiRenta.Domain.Returns;
using Microsoft.EntityFrameworkCore;

namespace MiRenta.Infrastructure.Data;

/// <summary>Base de datos propia de Mi renta (no comparte esquema ni conexión con el monolito de LinaTask).</summary>
public sealed class MiRentaDbContext(DbContextOptions<MiRentaDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<EmailToken> EmailTokens => Set<EmailToken>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ConsentRecord> Consents => Set<ConsentRecord>();
    public DbSet<Taxpayer> Taxpayers => Set<Taxpayer>();
    public DbSet<TaxCase> Cases => Set<TaxCase>();
    public DbSet<ClientAuthorization> ClientAuthorizations => Set<ClientAuthorization>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<ValidationIssue> ValidationIssues => Set<ValidationIssue>();
    public DbSet<TaxDocument> Documents => Set<TaxDocument>();
    public DbSet<DocumentExtraction> DocumentExtractions => Set<DocumentExtraction>();
    public DbSet<EvidenceItem> EvidenceItems => Set<EvidenceItem>();
    public DbSet<FinancialAccount> FinancialAccounts => Set<FinancialAccount>();
    public DbSet<FinancialTransaction> FinancialTransactions => Set<FinancialTransaction>();
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();
    public DbSet<TaxFact> Facts => Set<TaxFact>();
    public DbSet<TaxRule> Rules => Set<TaxRule>();
    public DbSet<TaxCalculation> Calculations => Set<TaxCalculation>();
    public DbSet<ReturnDraft> Drafts => Set<ReturnDraft>();
    public DbSet<ReturnLine> ReturnLines => Set<ReturnLine>();
    public DbSet<DraftReviewNote> DraftReviewNotes => Set<DraftReviewNote>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> Messages => Set<ConversationMessage>();
    public DbSet<AssistantTask> Tasks => Set<AssistantTask>();
    public DbSet<UserResponse> UserResponses => Set<UserResponse>();
    public DbSet<BackgroundJob> Jobs => Set<BackgroundJob>();
    public DbSet<OutboundEmail> Emails => Set<OutboundEmail>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("mi_renta");

        b.Entity<UserAccount>(e =>
        {
            e.ToTable("user_accounts");
            e.Property(x => x.Email).HasMaxLength(254);
            e.Property(x => x.DocumentNumber).HasMaxLength(20);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.HasIndex(x => x.Email).IsUnique();
            // Unicidad de identidad: una cédula = una cuenta (sin asociaciones silenciosas).
            e.HasIndex(x => new { x.DocumentType, x.DocumentNumber }).IsUnique();
            e.Ignore(x => x.IsEmailVerified);
        });
        b.Entity<EmailToken>(e =>
        {
            e.ToTable("email_tokens");
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Purpose });
        });
        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });
        b.Entity<ConsentRecord>(e => { e.ToTable("consent_records"); e.HasIndex(x => x.UserId); });

        b.Entity<Taxpayer>(e =>
        {
            e.ToTable("taxpayers");
            e.Property(x => x.DocumentNumber).HasMaxLength(20);
            e.HasIndex(x => new { x.OwnerUserId, x.DocumentType, x.DocumentNumber }).IsUnique();
        });
        b.Entity<TaxCase>(e =>
        {
            e.ToTable("tax_cases");
            e.Property(x => x.ReturnType).HasMaxLength(10);
            e.Property(x => x.Version).IsRowVersion();
            e.HasOne(x => x.Taxpayer).WithMany().HasForeignKey(x => x.TaxpayerId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.TaxpayerId, x.TaxYear, x.ReturnType }).IsUnique().HasFilter("deleted_at IS NULL");
            e.HasIndex(x => x.OwnerUserId);
        });
        b.Entity<ClientAuthorization>(e =>
        {
            e.ToTable("client_authorizations");
            e.HasIndex(x => new { x.CaseId, x.AccountantUserId });
            e.HasOne<TaxCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.Property(x => x.Action).HasMaxLength(80);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.CaseId, x.CreatedAt });
        });
        b.Entity<ValidationIssue>(e =>
        {
            e.ToTable("validation_issues");
            e.Property(x => x.Code).HasMaxLength(120);
            e.HasIndex(x => new { x.CaseId, x.Code, x.IsOpen });
            e.HasOne<TaxCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TaxDocument>(e =>
        {
            e.ToTable("documents");
            e.Property(x => x.OriginalFileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.StorageKey).HasMaxLength(300);
            e.Property(x => x.LastError).HasMaxLength(500);
            // Mismo archivo dos veces en el mismo expediente = duplicado (se devuelve el existente).
            e.HasIndex(x => new { x.CaseId, x.Sha256 }).IsUnique().HasFilter("deleted_at IS NULL");
            e.HasOne<TaxCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DocumentExtraction>(e =>
        {
            e.ToTable("document_extractions");
            e.Property(x => x.StructuredJson).HasColumnType("jsonb");
            e.Property(x => x.Confidence).HasPrecision(5, 4);
            e.HasIndex(x => new { x.DocumentId, x.Version }).IsUnique();
            e.HasOne<TaxDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<EvidenceItem>(e =>
        {
            e.ToTable("evidence_items");
            e.Property(x => x.Field).HasMaxLength(120);
            e.Property(x => x.RawText).HasMaxLength(500);
            e.Property(x => x.Locator).HasMaxLength(200);
            e.Property(x => x.ExtractedAmount).HasPrecision(18, 2);
            e.Property(x => x.ConfirmedAmount).HasPrecision(18, 2);
            e.Property(x => x.Confidence).HasPrecision(5, 4);
            e.HasIndex(x => new { x.CaseId, x.Status });
            e.HasOne<TaxDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<FinancialAccount>(e =>
        {
            e.ToTable("financial_accounts");
            e.Property(x => x.BalanceAtYearEnd).HasPrecision(18, 2);
            e.HasOne<TaxCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<FinancialTransaction>(e =>
        {
            e.ToTable("financial_transactions");
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.Counterparty).HasMaxLength(200);
            e.Property(x => x.DedupKey).HasMaxLength(64);
            e.HasIndex(x => new { x.CaseId, x.DedupKey }).IsUnique();
            e.HasIndex(x => new { x.CaseId, x.Date });
            e.HasOne<TaxCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Reconciliation>(e =>
        {
            e.ToTable("reconciliations");
            e.Property(x => x.TransactionIdsJson).HasColumnType("jsonb");
            e.Property(x => x.DedupKey).HasMaxLength(64);
            e.HasIndex(x => new { x.CaseId, x.DedupKey }).IsUnique();
            e.HasOne<TaxCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TaxFact>(e =>
        {
            e.ToTable("tax_facts");
            e.Property(x => x.Code).HasMaxLength(80);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.TextValue).HasMaxLength(500);
            e.Property(x => x.EvidenceIdsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.CaseId, x.Code, x.Version }).IsUnique();
            e.HasOne<TaxCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<TaxRule>(e =>
        {
            e.ToTable("tax_rules");
            e.Property(x => x.Code).HasMaxLength(80);
            e.Property(x => x.ParametersJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.TaxYear, x.RuleSetVersion, x.Code }).IsUnique();
            e.HasData(RuleCatalog2025.Build());
        });
        b.Entity<TaxCalculation>(e =>
        {
            e.ToTable("tax_calculations");
            e.Property(x => x.FactsSnapshotJson).HasColumnType("jsonb");
            e.Property(x => x.ResultJson).HasColumnType("jsonb");
            e.Property(x => x.BlockingReasonsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.CaseId, x.CreatedAt });
            e.HasOne<TaxCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ReturnDraft>(e =>
        {
            e.ToTable("return_drafts");
            e.HasIndex(x => new { x.CaseId, x.Version }).IsUnique();
            e.HasOne<TaxCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ReturnLine>(e =>
        {
            e.ToTable("return_lines");
            e.Property(x => x.Value).HasPrecision(18, 2);
            e.Property(x => x.SourcesJson).HasColumnType("jsonb");
            e.Property(x => x.WarningsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.DraftId, x.SortOrder });
            e.HasOne<ReturnDraft>().WithMany().HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<DraftReviewNote>(e =>
        {
            e.ToTable("draft_review_notes");
            e.HasOne<ReturnDraft>().WithMany().HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.HasIndex(x => x.CaseId).IsUnique();
            e.HasOne<TaxCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ConversationMessage>(e =>
        {
            e.ToTable("conversation_messages");
            e.Property(x => x.Content).HasMaxLength(4000);
            e.Property(x => x.IdempotencyKey).HasMaxLength(80);
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            e.HasIndex(x => new { x.ConversationId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AssistantTask>(e =>
        {
            e.ToTable("assistant_tasks");
            e.Property(x => x.Code).HasMaxLength(80);
            e.Property(x => x.OptionsJson).HasColumnType("jsonb");
            e.Property(x => x.RelatedJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.CaseId, x.Code }).IsUnique();
            e.HasOne<TaxCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserResponse>(e =>
        {
            e.ToTable("user_responses");
            e.Property(x => x.ValueJson).HasColumnType("jsonb");
            e.Property(x => x.IdempotencyKey).HasMaxLength(80);
            e.HasIndex(x => new { x.TaskId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            e.HasOne<AssistantTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BackgroundJob>(e =>
        {
            e.ToTable("background_jobs");
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.Property(x => x.DedupKey).HasMaxLength(120);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasIndex(x => x.DedupKey).IsUnique().HasFilter("dedup_key IS NOT NULL");
            e.HasIndex(x => new { x.Status, x.NextRunAt });
        });
        b.Entity<OutboundEmail>(e =>
        {
            e.ToTable("outbound_emails");
            e.Property(x => x.ToAddress).HasMaxLength(254);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.Subject).HasMaxLength(200);
            e.Property(x => x.DedupKey).HasMaxLength(160);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.HasIndex(x => x.DedupKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.ScheduledFor });
            e.HasIndex(x => new { x.UserId, x.Kind, x.CreatedAt });
        });
    }
}
