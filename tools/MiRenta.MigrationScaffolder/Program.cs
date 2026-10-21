// Genera migraciones de EF Core sin la herramienta global dotnet-ef (útil en entornos sin acceso a NuGet).
// Uso: dotnet run --project tools/MiRenta.MigrationScaffolder -- <NombreMigracion>
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.Extensions.DependencyInjection;
using MiRenta.Infrastructure.Data;

var name = args.FirstOrDefault() ?? throw new ArgumentException("Indica el nombre de la migración.");
var options = new DbContextOptionsBuilder<MiRentaDbContext>()
    .UseNpgsql("Host=localhost;Database=design_time_only", n => n.MigrationsHistoryTable("__ef_migrations_history", "mi_renta"))
    .UseSnakeCaseNamingConvention().Options;
using var context = new MiRentaDbContext(options);
var services = new ServiceCollection().AddEntityFrameworkDesignTimeServices().AddDbContextDesignTimeServices(context);
new Npgsql.EntityFrameworkCore.PostgreSQL.Design.Internal.NpgsqlDesignTimeServices().ConfigureDesignTimeServices(services);
using var sp = services.BuildServiceProvider();
var scaffolder = sp.GetRequiredService<IMigrationsScaffolder>();
var migration = scaffolder.ScaffoldMigration(name, "MiRenta.Infrastructure", "Migrations");
var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MiRenta.Infrastructure"));
var files = scaffolder.Save(projectDir, migration, Path.Combine(projectDir, "Migrations"));
Console.WriteLine($"Migración generada: {Path.GetFileName(files.MigrationFile)}");
