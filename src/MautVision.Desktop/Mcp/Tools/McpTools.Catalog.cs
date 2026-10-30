using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Desktop.Controllers;
using MautVision.Desktop.Controllers.Projects.Catalog;
using MautVision.Desktop.Controllers.Projects.Catalog.RequestDto;
using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    private const string VariantSchema = """
        { "type": "object", "description": "Project variant (a way objects look: day, night, mock-up): id, name, description, sortOrder, revision (pass it to changes), archivedAt (null when active).",
          "properties": { "id": { "type": "string" }, "name": { "type": "string" }, "description": { "type": "string" }, "sortOrder": { "type": "integer" },
            "revision": { "type": "integer" }, "archivedAt": { "type": ["string", "null"] } }, "required": ["id", "name", "revision"] }
        """;

    private const string ClassSchema = """
        { "type": "object", "description": "Class (an object to detect): id, name, description, sortOrder (the order gives YOLO indexes from 0), revision, archivedAt.",
          "properties": { "id": { "type": "string" }, "name": { "type": "string" }, "description": { "type": "string" }, "sortOrder": { "type": "integer" },
            "revision": { "type": "integer" }, "archivedAt": { "type": ["string", "null"] } }, "required": ["id", "name", "revision"] }
        """;

    private const string ClassVariantSchema = """
        { "type": "object", "description": "Class variant (a binding of a project variant to a class; classVariantId in other tools): id, classId, variantId, color (#rrggbb or null for the default), displayColor, revision, archivedAt, available (neither the binding, its class nor its variant is archived), assetCount (cut-out images), opaqueAssetCount (images without alpha), maxAssetWidth/maxAssetHeight.",
          "properties": { "id": { "type": "string" }, "classId": { "type": "string" }, "variantId": { "type": "string" }, "color": { "type": ["string", "null"] },
            "displayColor": { "type": "string" }, "revision": { "type": "integer" }, "archivedAt": { "type": ["string", "null"] }, "available": { "type": "boolean" },
            "assetCount": { "type": "integer" } }, "required": ["id", "classId", "variantId", "revision", "available"] }
        """;

    private const string DeletionResultSchema = """
        { "type": "object", "description": "What was deleted: rows of classes, classVariants and assets; cleanupPending — files are still being removed in the background.",
          "properties": { "classes": { "type": "integer" }, "classVariants": { "type": "integer" }, "assets": { "type": "integer" }, "cleanupPending": { "type": "boolean" } },
          "required": ["classes", "classVariants", "assets", "cleanupPending"] }
        """;

    private const string RevisionSchema = """{ "type": "integer", "description": "The revision you saw (from the list tool); a mismatch is *.revision_conflict. Omit to use the current one." }""";

    private const string JobStartedSchema = """
        { "type": "object", "description": "A started job: follow it with wait_job (projectId, jobId); its result (counts, rejected files with reasons) is job.result when it ends.",
          "properties": { "jobId": { "type": "string" }, "status": { "type": "string" }, "job": { "type": "object" } }, "required": ["jobId", "status", "job"] }
        """;

    private const string FileSourcesSchema = """
        "path": { "type": "string", "description": "Absolute path of a file or a folder on this computer." },
        "paths": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 10000, "description": "Absolute paths of files or folders." },
        "uploadId": { "type": "string", "description": "A file sent with upload_file." },
        "uploadIds": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 1000, "description": "Files sent with upload_file." },
        "recursive": { "type": "boolean", "default": false, "description": "For folders: include subfolders." },
        "operationId": { "type": "string", "description": "Optional key of this action: repeating a call with the same operationId returns the same job instead of importing twice." },
        "waitSeconds": { "type": "integer", "minimum": 0, "maximum": 120, "default": 0, "description": "Wait for the job up to this many seconds (progress is sent when a progressToken is given)." }
        """;


    private VariantsController? variantsController;
    private ClassesController? classesController;

    private VariantsController Variants => variantsController ??= new VariantsController(Core.Projects);

    private ClassesController Classes => classesController ??= new ClassesController(Core.Projects, Core.Jobs, importResources, importSelections, NoMcpDialogs);

    private IEnumerable<McpToolDefinition> CatalogTools()
    {
        // Variants.
        yield return McpToolDefinition.Create("list_variants", "List project variants", McpToolClass.Read, true,
            """
            Project variants: the ways objects look (day, night, mock-up), shared by all classes. A class gets a variant with
            bind_class_variant; cut-out images belong to such a binding. Archived variants are skipped unless includeArchived=true.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "includeArchived": { "type": "boolean", "default": false } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "variants": { "type": "array", "items": {{VariantSchema}} } }, "required": ["variants"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "includeArchived");
                var workspace = await WorkspaceAsync(call.Arguments.String("projectId"), call.CancellationToken);
                var includeArchived = call.Arguments.Bool("includeArchived", false);
                var variants = workspace.Variants.Where(item => includeArchived || item.ArchivedAt is null).ToArray();
                return Result(Wrap("variants", variants), variants.Length == 0
                    ? "No project variants."
                    : $"{variants.Length} variants: " + string.Join("; ", variants.Select(item => $"{item.Name} ({item.Id})")) + ".");
            });

        yield return McpToolDefinition.Create("create_variant", "Create project variant", McpToolClass.Write, false,
            """
            Creates a project variant. The name has 1-120 characters and is unique in the project case-insensitively, archive included
            (variants.name_duplicate); the description is up to 2000 characters.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "name": { "type": "string" }, "description": { "type": "string", "default": "" } },
              "required": ["projectId", "name"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "variant": {{VariantSchema}} }, "required": ["variant"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "name", "description");
                var projectId = await PreparedIdAsync(call);
                var variant = await Variants.CreateAsync(new CreateVariantRequestDto(projectId, call.Arguments.String("name"),
                    call.Arguments.OptionalString("description") ?? ""), call.CancellationToken);
                return Result(Wrap("variant", variant), $"Variant {variant.Name} created with id {variant.Id}.");
            });

        yield return McpToolDefinition.Create("update_variant", "Update project variant", McpToolClass.Write, true,
            """
            Renames a project variant or changes its description; omitted fields keep their values. An archived variant is read-only
            (variants.archived): restore it with archive_variant(archived=false) first.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "variantId": { "type": "string" }, "revision": {{RevisionSchema}},
                "name": { "type": "string" }, "description": { "type": "string" } },
              "required": ["projectId", "variantId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "variant": {{VariantSchema}} }, "required": ["variant"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "variantId", "revision", "name", "description");
                var projectId = call.Arguments.String("projectId");
                var current = await FindVariantAsync(projectId, call.Arguments.Guid("variantId"), call.CancellationToken);
                var variant = await Variants.UpdateAsync(new UpdateVariantRequestDto(projectId, current.Id, Revision(call.Arguments, current.Revision),
                    call.Arguments.OptionalString("name") ?? current.Name, call.Arguments.OptionalString("description") ?? current.Description), call.CancellationToken);
                return Result(Wrap("variant", variant), $"Variant {variant.Name} saved (revision {variant.Revision}).");
            });

        yield return McpToolDefinition.Create("archive_variant", "Archive project variant", McpToolClass.Write, true,
            """
            Archives a project variant (archived=true, the default) or restores it. Nothing is deleted; bindings of an archived variant become
            unavailable: no new images, not offered for new annotation. Only the variant itself changes, not its bindings.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "variantId": { "type": "string" }, "archived": { "type": "boolean", "default": true },
                "revision": {{RevisionSchema}} }, "required": ["projectId", "variantId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "variant": {{VariantSchema}} }, "required": ["variant"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "variantId", "archived", "revision");
                var projectId = call.Arguments.String("projectId");
                var current = await FindVariantAsync(projectId, call.Arguments.Guid("variantId"), call.CancellationToken);
                var archived = call.Arguments.Bool("archived", true);
                var variant = await Variants.SetArchivedAsync(new SetVariantArchivedRequestDto(projectId, current.Id, Revision(call.Arguments, current.Revision), archived),
                    call.CancellationToken);
                return Result(Wrap("variant", variant), archived ? $"Variant {variant.Name} is archived." : $"Variant {variant.Name} is restored.");
            });

        yield return McpToolDefinition.Create("delete_variant", "Delete project variant", McpToolClass.Destructive, false,
            """
            Permanently deletes a project variant with its bindings in all classes and their cut-out images, like "Delete" in the window: the
            content is reviewed first and blockers refuse the deletion (variants.deletion_blocked with details.blockers: running jobs, current
            annotation that refers to it, generation presets with a share above 0%). Prefer archive_variant when the data may be needed.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "variantId": { "type": "string" } }, "required": ["projectId", "variantId"],
              "additionalProperties": false }
            """,
            DeletionResultSchema,
            async call =>
            {
                call.Arguments.Only("projectId", "variantId");
                var projectId = await PreparedIdAsync(call);
                var variantId = call.Arguments.Guid("variantId");
                var review = await Variants.ReviewDeletionAsync(new ReviewVariantDeletionRequestDto(projectId, variantId), call.CancellationToken);
                ThrowIfBlocked("variants", review.TargetName, review.Blockers);
                var result = await Variants.DeleteAsync(new DeleteVariantRequestDto(projectId, variantId, review.Revision), call.CancellationToken);
                return Result(McpToolResult.Object(result), $"Variant {review.TargetName} deleted with {result.ClassVariants} bindings and {result.Assets} images.");
            });

        // Classes.
        yield return McpToolDefinition.Create("list_classes", "List classes", McpToolClass.Read, true,
            """
            Classes of the project in their order (sortOrder gives YOLO indexes from 0 when a dataset is built), each with its variant
            bindings (classVariants: id is the classVariantId of the asset tools and of annotation, variantName, color, assetCount). Archived
            classes are skipped unless includeArchived=true. classOrderRevision is what reorder_classes expects.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "includeArchived": { "type": "boolean", "default": false } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": {
                "classes": { "type": "array", "items": { "allOf": [{{ClassSchema}}, { "type": "object", "properties": { "classVariants": { "type": "array", "items": {{ClassVariantSchema}} } } }] } },
                "classOrderRevision": { "type": "integer" }, "catalogRevision": { "type": "integer" } },
              "required": ["classes", "classOrderRevision", "catalogRevision"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "includeArchived");
                var workspace = await WorkspaceAsync(call.Arguments.String("projectId"), call.CancellationToken);
                var includeArchived = call.Arguments.Bool("includeArchived", false);
                var variants = workspace.Variants.ToDictionary(item => item.Id);
                var classes = new JsonArray();
                foreach (var item in workspace.Classes.Where(item => includeArchived || item.ArchivedAt is null))
                {
                    var node = McpToolResult.Object(item);
                    node["classVariants"] = new JsonArray(workspace.ClassVariants.Where(binding => binding.ClassId == item.Id)
                        .Where(binding => includeArchived || binding.Available)
                        .Select(binding =>
                        {
                            var bound = McpToolResult.Object(binding);
                            bound["variantName"] = variants.TryGetValue(binding.VariantId, out var variant) ? variant.Name : "";
                            return (JsonNode?)bound;
                        }).ToArray());
                    classes.Add(node);
                }
                var structured = new JsonObject
                {
                    ["classes"] = classes, ["classOrderRevision"] = workspace.ClassOrderRevision, ["catalogRevision"] = workspace.CatalogRevision,
                };
                return Result(structured, classes.Count == 0
                    ? "No classes."
                    : $"{classes.Count} classes: " + string.Join("; ", classes.Select(item => $"{item!["name"]} ({item["id"]}, {item["classVariants"]!.AsArray().Count} variants)")) + ".");
            });

        yield return McpToolDefinition.Create("create_class", "Create class", McpToolClass.Write, false,
            """
            Creates a class at the end of the order. The name has 1-120 characters and is unique case-insensitively, archive included
            (classes.name_duplicate). A class needs at least one variant binding (bind_class_variant) to hold cut-out images; annotation may
            refer to the class alone.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "name": { "type": "string" }, "description": { "type": "string", "default": "" } },
              "required": ["projectId", "name"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "class": {{ClassSchema}} }, "required": ["class"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "name", "description");
                var projectId = await PreparedIdAsync(call);
                var created = await Classes.CreateAsync(new CreateClassRequestDto(projectId, call.Arguments.String("name"), call.Arguments.OptionalString("description") ?? ""),
                    call.CancellationToken);
                return Result(Wrap("class", created), $"Class {created.Name} created with id {created.Id}.");
            });

        yield return McpToolDefinition.Create("update_class", "Update class", McpToolClass.Write, true,
            """
            Renames a class or changes its description; omitted fields keep their values. An archived class is read-only (classes.archived).
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "classId": { "type": "string" }, "revision": {{RevisionSchema}},
                "name": { "type": "string" }, "description": { "type": "string" } },
              "required": ["projectId", "classId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "class": {{ClassSchema}} }, "required": ["class"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "classId", "revision", "name", "description");
                var projectId = call.Arguments.String("projectId");
                var current = await FindClassAsync(projectId, call.Arguments.Guid("classId"), call.CancellationToken);
                var updated = await Classes.UpdateAsync(new UpdateClassRequestDto(projectId, current.Id, Revision(call.Arguments, current.Revision),
                    call.Arguments.OptionalString("name") ?? current.Name, call.Arguments.OptionalString("description") ?? current.Description), call.CancellationToken);
                return Result(Wrap("class", updated), $"Class {updated.Name} saved (revision {updated.Revision}).");
            });

        yield return McpToolDefinition.Create("archive_class", "Archive class", McpToolClass.Write, true,
            """
            Archives a class (archived=true, the default) or restores it. Nothing is deleted: the class keeps its place in the order and its
            bindings, but is not offered for new annotation and its bindings take no new images.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "classId": { "type": "string" }, "archived": { "type": "boolean", "default": true },
                "revision": {{RevisionSchema}} }, "required": ["projectId", "classId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "class": {{ClassSchema}} }, "required": ["class"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "classId", "archived", "revision");
                var projectId = call.Arguments.String("projectId");
                var current = await FindClassAsync(projectId, call.Arguments.Guid("classId"), call.CancellationToken);
                var archived = call.Arguments.Bool("archived", true);
                var updated = await Classes.SetArchivedAsync(new SetClassArchivedRequestDto(projectId, current.Id, Revision(call.Arguments, current.Revision), archived),
                    call.CancellationToken);
                return Result(Wrap("class", updated), archived ? $"Class {updated.Name} is archived." : $"Class {updated.Name} is restored.");
            });

        yield return McpToolDefinition.Create("reorder_classes", "Reorder classes", McpToolClass.Write, true,
            """
            Sets the class order: classIds must list every class of the project exactly once, archived ones included (classes.order_invalid).
            The order gives YOLO class indexes from 0 in new datasets; built datasets and models keep their own map. Pass orderRevision
            (classOrderRevision of list_classes) to detect a concurrent change (classes.order_revision_conflict); omit it to use the current one.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}},
                "classIds": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 10000 },
                "orderRevision": { "type": "integer" } },
              "required": ["projectId", "classIds"], "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "classes": { "type": "array", "items": {{ClassSchema}} }, "orderRevision": { "type": "integer" } },
              "required": ["classes", "orderRevision"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "classIds", "orderRevision");
                var projectId = call.Arguments.String("projectId");
                var ids = GuidList(call.Arguments, "classIds", 1, 10_000);
                var orderRevision = call.Arguments.Has("orderRevision")
                    ? Long(call.Arguments, "orderRevision")
                    : (await WorkspaceAsync(projectId, call.CancellationToken)).ClassOrderRevision;
                var order = await Classes.ReorderAsync(new ReorderClassesRequestDto(projectId, ids, orderRevision), call.CancellationToken);
                return Result(McpToolResult.Object(order), "Class order: " + string.Join(", ", order.Classes.Select(item => item.Name)) + ".");
            });

        yield return McpToolDefinition.Create("delete_class", "Delete class", McpToolClass.Destructive, false,
            """
            Permanently deletes a class with its variant bindings and their cut-out images, like "Delete" in the window: the content is
            reviewed first and blockers refuse it (classes.deletion_blocked with details.blockers: running jobs, current annotation of images
            that uses the class, generation presets). Prefer archive_class when the data may be needed.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "classId": { "type": "string" } }, "required": ["projectId", "classId"],
              "additionalProperties": false }
            """,
            DeletionResultSchema,
            async call =>
            {
                call.Arguments.Only("projectId", "classId");
                var projectId = await PreparedIdAsync(call);
                var classId = call.Arguments.Guid("classId");
                var review = await Classes.ReviewDeletionAsync(new ReviewClassDeletionRequestDto(projectId, classId), call.CancellationToken);
                ThrowIfBlocked("classes", review.TargetName, review.Blockers);
                var result = await Classes.DeleteAsync(new DeleteClassRequestDto(projectId, classId, review.Revision), call.CancellationToken);
                return Result(McpToolResult.Object(result), $"Class {review.TargetName} deleted with {result.ClassVariants} bindings and {result.Assets} images.");
            });

        // Bindings of variants to classes.
        yield return McpToolDefinition.Create("bind_class_variant", "Bind variant to class", McpToolClass.Write, false,
            """
            Attaches a project variant to a class (once per class; classes.duplicate_variant_binding otherwise). The binding id is the
            classVariantId that holds cut-out images (import_class_assets) and that annotation boxes may name. color is the box colour
            #rrggbb; omit it for the palette colour. The class and the variant must be active.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "classId": { "type": "string" }, "variantId": { "type": "string" },
                "color": { "type": "string", "pattern": "^#[0-9a-fA-F]{6}$" } },
              "required": ["projectId", "classId", "variantId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "classVariant": {{ClassVariantSchema}} }, "required": ["classVariant"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "classId", "variantId", "color");
                var projectId = await PreparedIdAsync(call);
                var binding = await Classes.AddVariantAsync(new AddClassVariantRequestDto(projectId, call.Arguments.Guid("classId"), call.Arguments.Guid("variantId")),
                    call.CancellationToken);
                if (call.Arguments.OptionalString("color") is { } color)
                    binding = await Classes.SetVariantColorAsync(new SetClassVariantColorRequestDto(projectId, binding.Id, binding.Revision, color), call.CancellationToken);
                return Result(Wrap("classVariant", binding), $"Variant bound to the class: classVariantId {binding.Id}, colour {binding.DisplayColor}.");
            });

        yield return McpToolDefinition.Create("update_class_variant", "Update class variant", McpToolClass.Write, true,
            """
            Changes a class variant binding: color (#rrggbb, or null for the palette colour) and/or archived. Omitted fields keep their values.
            The colour of an archived binding cannot change (classes.variant_archived); restoring needs an active class and variant.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "classVariantId": { "type": "string" }, "revision": {{RevisionSchema}},
                "color": { "type": ["string", "null"], "pattern": "^#[0-9a-fA-F]{6}$" }, "archived": { "type": "boolean" } },
              "required": ["projectId", "classVariantId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "classVariant": {{ClassVariantSchema}} }, "required": ["classVariant"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "classVariantId", "revision", "color", "archived");
                var projectId = call.Arguments.String("projectId");
                var binding = await FindBindingAsync(projectId, call.Arguments.Guid("classVariantId"), call.CancellationToken);
                var revision = Revision(call.Arguments, binding.Revision);
                var archived = call.Arguments.Has("archived") ? call.Arguments.Bool("archived", false) : (bool?)null;
                var colorGiven = call.Arguments.Raw.ContainsKey("color");
                var color = colorGiven ? call.Arguments.OptionalString("color") : null;
                if (!colorGiven && archived is null) throw McpToolException.InvalidArgument("color", "pass color or archived");
                // Restoring comes before the colour (an archived binding keeps its colour), archiving after it.
                if (archived == false && binding.ArchivedAt is not null)
                {
                    binding = await Classes.SetVariantArchivedAsync(new SetClassVariantArchivedRequestDto(projectId, binding.Id, revision, false), call.CancellationToken);
                    revision = binding.Revision;
                }
                if (colorGiven)
                {
                    binding = await Classes.SetVariantColorAsync(new SetClassVariantColorRequestDto(projectId, binding.Id, revision, color), call.CancellationToken);
                    revision = binding.Revision;
                }
                if (archived == true && binding.ArchivedAt is null)
                    binding = await Classes.SetVariantArchivedAsync(new SetClassVariantArchivedRequestDto(projectId, binding.Id, revision, true), call.CancellationToken);
                return Result(Wrap("classVariant", binding), $"Class variant {binding.Id} saved: colour {binding.DisplayColor}, "
                    + (binding.ArchivedAt is null ? "active." : "archived."));
            });

        yield return McpToolDefinition.Create("unbind_class_variant", "Unbind variant from class", McpToolClass.Destructive, false,
            """
            Permanently removes a class variant binding with all its cut-out images (the class and the project variant stay). The content is
            reviewed first and blockers refuse it (classes.deletion_blocked with details.blockers: running jobs, current annotation that uses
            this classVariantId, generation presets). To hide it without deleting, use update_class_variant(archived=true).
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "classVariantId": { "type": "string" } }, "required": ["projectId", "classVariantId"],
              "additionalProperties": false }
            """,
            DeletionResultSchema,
            async call =>
            {
                call.Arguments.Only("projectId", "classVariantId");
                var projectId = await PreparedIdAsync(call);
                var bindingId = call.Arguments.Guid("classVariantId");
                var review = await Classes.ReviewVariantDeletionAsync(new ReviewClassVariantDeletionRequestDto(projectId, bindingId), call.CancellationToken);
                ThrowIfBlocked("classes", review.TargetName, review.Blockers);
                var result = await Classes.DeleteVariantAsync(new DeleteClassVariantRequestDto(projectId, bindingId, review.Revision), call.CancellationToken);
                return Result(McpToolResult.Object(result), $"Class variant {review.TargetName} removed with {result.Assets} images.");
            });

        // Cut-out images of bindings.
        yield return McpToolDefinition.Create("list_class_assets", "List class images", McpToolClass.Read, true,
            """
            Cut-out images (class assets) of a class variant binding, in import order, a page at a time: pass nextCursor as cursor for the next
            page. width and height are pixels with EXIF orientation; hasAlpha tells whether the file has an alpha channel (generation pastes
            cut-outs with transparency best). Look at one with view({ classAsset: id }).
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "classVariantId": { "type": "string" },
                "limit": { "type": "integer", "minimum": 1, "maximum": 500, "default": 100 }, "cursor": { "type": "string" } },
              "required": ["projectId", "classVariantId"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": {
                "items": { "type": "array", "items": { "type": "object", "properties": { "id": { "type": "string" }, "classVariantId": { "type": "string" },
                  "format": { "type": "string" }, "width": { "type": "integer" }, "height": { "type": "integer" }, "hasAlpha": { "type": "boolean" },
                  "byteSize": { "type": "integer" }, "createdAt": { "type": "string" } }, "required": ["id", "width", "height"] } },
                "nextCursor": { "type": ["string", "null"] } },
              "required": ["items", "nextCursor"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "classVariantId", "limit", "cursor");
                var projectId = await PreparedIdAsync(call);
                var page = await Classes.ListAssetsAsync(new ListClassAssetsRequestDto(projectId, call.Arguments.Guid("classVariantId"),
                    call.Arguments.Int("limit", 1, 500, 100), call.Arguments.OptionalString("cursor")), call.CancellationToken);
                return Result(WithoutUrls(McpToolResult.Object(page)), $"{page.Items.Length} images" + (page.NextCursor is null ? "." : "; more with cursor."));
            });

        yield return McpToolDefinition.Create("import_class_assets", "Import class images", McpToolClass.Write, false,
            $$"""
            Imports cut-out images (PNG, JPEG, WebP, BMP; PNG with transparency is best) into a class variant binding as a job. Give files or
            folders by absolute path (path or paths; folders contribute their image files, with recursive also of subfolders) or files sent with
            upload_file (uploadId or uploadIds). Identical content in the binding is skipped; damaged files are rejected with a reason. Returns
            { jobId, status, job } at once (or after waitSeconds); follow it with wait_job, the counts are in job.result. The binding must be
            available (classes.variant_unavailable).
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "classVariantId": { "type": "string" }, {{FileSourcesSchema}} },
              "required": ["projectId", "classVariantId"], "additionalProperties": false }
            """,
            JobStartedSchema,
            async call =>
            {
                call.Arguments.Only("projectId", "classVariantId", "path", "paths", "uploadId", "uploadIds", "recursive", "operationId", "waitSeconds");
                var projectId = await PreparedIdAsync(call);
                var waitSeconds = call.Arguments.Int("waitSeconds", 0, McpJobWaiter.MaxWaitSeconds, 0);
                var files = ImportFiles(call.Arguments, expandFolders: true);
                var token = importSelections.Register(files);
                var job = await Classes.ImportAssetsAsync(new ImportClassAssetsRequestDto(projectId, call.Arguments.Guid("classVariantId"), token, OperationId(call.Arguments)),
                    call.CancellationToken);
                return await McpJobWaiter.StartedAsync(Core.Jobs, await Core.Jobs.GetAsync(projectId, job.Id, call.CancellationToken), waitSeconds, call,
                    $"Import of {files.Count} class images");
            });

        yield return McpToolDefinition.Create("delete_class_assets", "Delete class images", McpToolClass.Destructive, false,
            """
            Permanently deletes cut-out images of class variants (up to 500 ids from list_class_assets). Every image is reviewed first; if any
            is blocked (for example a running job of the project) nothing is deleted and the error is classes.deletion_blocked with
            details.blockers.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}},
                "assetIds": { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 500 } },
              "required": ["projectId", "assetIds"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "deleted": { "type": "integer" }, "cleanupPending": { "type": "boolean" } }, "required": ["deleted", "cleanupPending"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "assetIds");
                var projectId = await PreparedIdAsync(call);
                var ids = GuidList(call.Arguments, "assetIds", 1, 500).Distinct().ToArray();
                var reviews = new List<DeletionReviewResponseDto>();
                foreach (var id in ids)
                {
                    reviews.Add(await Classes.ReviewAssetDeletionAsync(new ReviewClassAssetDeletionRequestDto(projectId, id), call.CancellationToken));
                }
                ThrowIfBlocked("classes", "", reviews.SelectMany(review => review.Blockers).ToArray());
                long deleted = 0;
                var pending = false;
                foreach (var review in reviews)
                {
                    var result = await Classes.DeleteAssetAsync(new DeleteClassAssetRequestDto(projectId, review.TargetId, review.Revision), call.CancellationToken);
                    deleted += result.Assets;
                    pending |= result.CleanupPending;
                }
                return Result(new JsonObject { ["deleted"] = deleted, ["cleanupPending"] = pending }, $"{deleted} class images deleted.");
            });
    }

    private async Task<CatalogWorkspaceResponseDto> WorkspaceAsync(string projectId, CancellationToken cancellationToken)
    {
        var project = await PrepareAsync(projectId, cancellationToken);
        return await Classes.GetWorkspaceAsync(new GetCatalogWorkspaceRequestDto(project.Id), cancellationToken);
    }

    private async Task<VariantResponseDto> FindVariantAsync(string projectId, Guid variantId, CancellationToken cancellationToken)
        => (await WorkspaceAsync(projectId, cancellationToken)).Variants.FirstOrDefault(item => item.Id == variantId)
            ?? throw new CoreException("variants.not_found", $"Variant {variantId} is not in the project.");

    private async Task<ClassResponseDto> FindClassAsync(string projectId, Guid classId, CancellationToken cancellationToken)
        => (await WorkspaceAsync(projectId, cancellationToken)).Classes.FirstOrDefault(item => item.Id == classId)
            ?? throw new CoreException("classes.not_found", $"Class {classId} is not in the project.");

    private async Task<ClassVariantResponseDto> FindBindingAsync(string projectId, Guid bindingId, CancellationToken cancellationToken)
        => (await WorkspaceAsync(projectId, cancellationToken)).ClassVariants.FirstOrDefault(item => item.Id == bindingId)
            ?? throw new CoreException("classes.not_found", $"Class variant {bindingId} is not in the project.");

}
