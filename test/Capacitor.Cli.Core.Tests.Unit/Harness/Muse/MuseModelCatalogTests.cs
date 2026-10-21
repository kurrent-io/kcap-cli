using Capacitor.Cli.Core.Harness.Muse;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

public class MuseModelCatalogTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Meta = """
        {"schema_version":2,"provider_id":"meta","profile_id":"tbh","source":"provider_catalog","rows":[
          {"model_id":"muse-wonder-1.0","display_label":"muse-wonder-1.0","visibility":"visible","release_date":"2026-10-12","display_order":null,"is_current":false,"is_default":true},
          {"model_id":"muse-wonder-1.0-contributor","display_label":"muse-wonder-1.0-contributor","visibility":"hidden","release_date":"2026-10-12","display_order":null},
          {"model_id":"muse-spark-1.3","display_label":"muse-spark-1.3","visibility":"visible","release_date":"2026-09-02","display_order":null},
          {"model_id":"muse-image-1.0","display_label":"Muse Image","visibility":"hidden","release_date":"2026-08-25","display_order":null},
          {"model_id":"muse-spark-1.2","display_label":"muse-spark-1.2","visibility":"visible","release_date":"2026-08-05","display_order":null,"is_current":true}]}
        """;

    string Dir => Tmp.PathTo("model-catalog");

    void Write(string name, string json) => Tmp.CreateFile(["model-catalog", name], json);

    [Test]
    public async Task Visible_rows_newest_first_hidden_rows_dropped() {
        Write("6d657461__p746268.json", Meta);

        var options = MuseModelCatalog.Read(Dir)!;

        await Assert.That(options.Select(o => o.Value))
            .IsEquivalentTo(["muse-wonder-1.0", "muse-spark-1.3", "muse-spark-1.2"], CollectionOrdering.Matching);
        await Assert.That(options.Select(o => o.Label))
            .IsEquivalentTo(["muse-wonder-1.0", "muse-spark-1.3", "muse-spark-1.2"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Display_order_wins_over_release_date_and_a_blank_label_falls_back_to_the_id() {
        Write("a.json", """
            {"schema_version":2,"provider_id":"meta","rows":[
              {"model_id":"old","display_label":"","visibility":"visible","release_date":"2025-01-01","display_order":1},
              {"model_id":"new","display_label":"New","visibility":"visible","release_date":"2026-01-01","display_order":2},
              {"model_id":"unordered","display_label":"Unordered","visibility":"visible","release_date":"2026-06-01","display_order":null}]}
            """);

        var options = MuseModelCatalog.Read(Dir)!;

        await Assert.That(options.Select(o => o.Value)).IsEquivalentTo(["old", "new", "unordered"], CollectionOrdering.Matching);
        await Assert.That(options[0].Label).IsEqualTo("old");
    }

    /// <summary>The first file by name owns a shared id even when a later file would sort it earlier:
    /// the row is chosen before the ordering is applied, not after.</summary>
    [Test]
    public async Task Several_profiles_merge_by_id_in_file_name_order() {
        Write("b.json", """{"schema_version":2,"provider_id":"meta","rows":[{"model_id":"shared","display_label":"From b","visibility":"visible","release_date":"2026-12-01","display_order":1}]}""");
        Write("a.json", """{"schema_version":2,"provider_id":"meta","rows":[{"model_id":"shared","display_label":"From a","visibility":"visible","release_date":"2026-01-01","display_order":20},{"model_id":"only-a","display_label":"Only a","visibility":"visible","release_date":"2025-01-01","display_order":2}]}""");

        var options = MuseModelCatalog.Read(Dir)!;

        await Assert.That(options.Select(o => o.Value)).IsEquivalentTo(["only-a", "shared"], CollectionOrdering.Matching);
        await Assert.That(options.Single(o => o.Value == "shared").Label).IsEqualTo("From a");
        await Assert.That(MuseModelCatalog.Files(Dir).Select(f => Path.GetFileName(f)!)).IsEquivalentTo(["a.json", "b.json"], CollectionOrdering.Matching);
    }

    /// <summary>Every hosted launch names the meta provider, so a catalog cached for another provider
    /// must not put its models in the picker.</summary>
    [Test]
    public async Task Another_providers_catalog_contributes_nothing() {
        Write("meta.json", """{"schema_version":2,"provider_id":"meta","rows":[{"model_id":"muse-spark-1.3","display_label":"muse-spark-1.3","visibility":"visible","release_date":"2026-09-02"}]}""");
        Write("other.json", """{"schema_version":2,"provider_id":"other","rows":[{"model_id":"other-model","display_label":"Other","visibility":"visible","release_date":"2026-12-01"}]}""");

        var options = MuseModelCatalog.Read(Dir)!;

        await Assert.That(options.Select(o => o.Value)).IsEquivalentTo(["muse-spark-1.3"]);
    }

    [Test]
    [Arguments("""{"schema_version":3,"provider_id":"meta","rows":[{"model_id":"x","visibility":"visible"}]}""")]
    [Arguments("""{"provider_id":"meta","rows":[{"model_id":"x","visibility":"visible"}]}""")]
    [Arguments("""{"schema_version":2,"rows":[{"model_id":"x","visibility":"visible"}]}""")]
    [Arguments("""{"schema_version":2,"provider_id":"other","rows":[{"model_id":"x","visibility":"visible"}]}""")]
    [Arguments("""{"schema_version":2,"provider_id":"meta","rows":[{"model_id":"x","visibility":"hidden"},{"visibility":"visible"},"junk"]}""")]
    [Arguments("""{"schema_version":2,"provider_id":"meta","rows":"nope"}""")]
    [Arguments("""[1,2]""")]
    [Arguments("""not json""")]
    public async Task A_document_that_is_not_a_usable_catalog_yields_null(string json) {
        Write("a.json", json);

        await Assert.That(MuseModelCatalog.Read(Dir)).IsNull();
    }

    [Test]
    public async Task No_directory_or_no_files_is_null_and_lists_no_files() {
        await Assert.That(MuseModelCatalog.Read(Dir)).IsNull();
        await Assert.That(MuseModelCatalog.Files(Dir)).IsEmpty();

        Tmp.CreateFile(["model-catalog", "notes.txt"], "ignored");

        await Assert.That(MuseModelCatalog.Read(Dir)).IsNull();
        await Assert.That(MuseModelCatalog.Files(Dir)).IsEmpty();
    }
}
