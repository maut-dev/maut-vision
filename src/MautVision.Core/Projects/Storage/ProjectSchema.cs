using Microsoft.Data.Sqlite;

namespace MautVision.Core.Projects.Storage;

/// <summary>One step of project.db schema: brings the database to Version inside the open transaction.</summary>
internal sealed record SchemaMigration(int Version, Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> ApplyAsync)
{
    internal static SchemaMigration Sql(int version, string sql) => new(version, async (connection, transaction, cancellationToken) =>
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    });
}

/// <summary>Ordered schema of project.db. A new step gets the next version and is appended; applied steps never change.</summary>
internal static class ProjectSchema
{
    internal static IReadOnlyList<SchemaMigration> Migrations { get; } =
    [
        SchemaMigration.Sql(1, """
            CREATE TABLE project_meta (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            ) STRICT, WITHOUT ROWID;
            """),
        // Jobs: seq gives insertion order for listing; the partial index keeps recovery at open one small query
        // regardless of history size. Times are Unix milliseconds UTC.
        SchemaMigration.Sql(2, """
            CREATE TABLE jobs (
                seq INTEGER PRIMARY KEY,
                id TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL,
                operation_id TEXT NOT NULL UNIQUE,
                input TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN ('queued', 'running', 'succeeded', 'failed', 'cancelled', 'interrupted', 'blocked')),
                progress REAL CHECK (progress IS NULL OR progress BETWEEN 0 AND 100),
                progress_text TEXT,
                processed INTEGER,
                total INTEGER,
                error_code TEXT,
                error_message TEXT,
                cancel_requested INTEGER NOT NULL DEFAULT 0 CHECK (cancel_requested IN (0, 1)),
                checkpoint TEXT,
                revision INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                started_at INTEGER,
                finished_at INTEGER
            ) STRICT;
            CREATE INDEX jobs_unfinished ON jobs (seq) WHERE status IN ('queued', 'running', 'blocked');
            CREATE TABLE job_steps (
                job_id TEXT NOT NULL REFERENCES jobs (id) ON DELETE CASCADE,
                step_key TEXT NOT NULL,
                PRIMARY KEY (job_id, step_key)
            ) STRICT, WITHOUT ROWID;
            """),
        // Catalog: variants, classes, their bindings and source images; name_key is the C# case-folded name
        // (SQLite lower() folds only ASCII). class_assets.seq is AUTOINCREMENT so that a keyset cursor and the
        // deletion review never see a reused number. file_cleanup keeps files whose deletion failed after commit.
        // jobs.result holds the summary a job kind defines (an import adds, skips and rejects files).
        SchemaMigration.Sql(3, """
            CREATE TABLE catalog_state (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                class_order_revision INTEGER NOT NULL,
                catalog_revision INTEGER NOT NULL
            ) STRICT;
            INSERT INTO catalog_state (id, class_order_revision, catalog_revision) VALUES (1, 1, 1);
            CREATE TABLE variants (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                name_key TEXT NOT NULL UNIQUE,
                description TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                archived_at INTEGER,
                revision INTEGER NOT NULL
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX variants_order ON variants (sort_order);
            CREATE TABLE classes (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                name_key TEXT NOT NULL UNIQUE,
                description TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                archived_at INTEGER,
                revision INTEGER NOT NULL
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX classes_order ON classes (sort_order);
            CREATE TABLE class_variants (
                id TEXT NOT NULL PRIMARY KEY,
                class_id TEXT NOT NULL REFERENCES classes (id) ON DELETE CASCADE,
                variant_id TEXT NOT NULL REFERENCES variants (id) ON DELETE CASCADE,
                color TEXT CHECK (color IS NULL OR (length(color) = 7 AND color GLOB '#[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]')),
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                archived_at INTEGER,
                revision INTEGER NOT NULL,
                asset_count INTEGER NOT NULL DEFAULT 0,
                opaque_asset_count INTEGER NOT NULL DEFAULT 0,
                max_asset_width INTEGER NOT NULL DEFAULT 0,
                max_asset_height INTEGER NOT NULL DEFAULT 0,
                UNIQUE (class_id, variant_id)
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX class_variants_variant ON class_variants (variant_id);
            CREATE TABLE class_assets (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                class_variant_id TEXT NOT NULL REFERENCES class_variants (id) ON DELETE CASCADE,
                format TEXT NOT NULL CHECK (format IN ('png', 'jpeg', 'webp', 'bmp')),
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                has_alpha INTEGER NOT NULL CHECK (has_alpha IN (0, 1)),
                byte_size INTEGER NOT NULL CHECK (byte_size > 0),
                sha256 TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                operation_id TEXT NOT NULL,
                UNIQUE (class_variant_id, sha256)
            ) STRICT;
            CREATE INDEX class_assets_page ON class_assets (class_variant_id, seq);
            CREATE TABLE file_cleanup (
                kind TEXT NOT NULL,
                id TEXT NOT NULL,
                extension TEXT NOT NULL,
                queued_at INTEGER NOT NULL,
                PRIMARY KEY (kind, id, extension)
            ) STRICT, WITHOUT ROWID;
            ALTER TABLE jobs ADD COLUMN result TEXT;
            """),
        // Images: groups, images and immutable annotation revisions. images.ordinal is the import order inside a group
        // (keyset pages); next_ordinal of the group hands out the following numbers. The page indexes cover every filter
        // combination of the gallery (archived, review state) in ordinal order. object_count and review_state repeat the
        // current annotation for the tiles and filters. Annotation objects are rows per revision; the class and class
        // variant indexes answer the catalog deletion check. jobs_image_import_group lists the import history of a group.
        SchemaMigration.Sql(4, """
            CREATE TABLE image_groups (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                origin TEXT NOT NULL CHECK (origin IN ('manual', 'generated')),
                split_hint TEXT NOT NULL CHECK (split_hint IN ('unassigned', 'train', 'validation', 'test')),
                generation_run_id TEXT,
                generation_state TEXT CHECK (generation_state IS NULL OR generation_state IN ('producing', 'completed', 'partial')),
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                archived_at INTEGER,
                revision INTEGER NOT NULL,
                image_count INTEGER NOT NULL DEFAULT 0,
                archived_image_count INTEGER NOT NULL DEFAULT 0,
                next_ordinal INTEGER NOT NULL DEFAULT 1
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX image_groups_created ON image_groups (created_at, id);
            CREATE TABLE images (
                id TEXT NOT NULL PRIMARY KEY,
                group_id TEXT NOT NULL REFERENCES image_groups (id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL,
                display_name TEXT NOT NULL,
                format TEXT NOT NULL CHECK (format IN ('png', 'jpeg', 'webp', 'bmp')),
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                byte_size INTEGER NOT NULL CHECK (byte_size > 0),
                sha256 TEXT NOT NULL,
                pixel_hash TEXT NOT NULL,
                revision INTEGER NOT NULL,
                annotation_revision INTEGER NOT NULL,
                object_count INTEGER NOT NULL,
                review_state TEXT NOT NULL CHECK (review_state IN ('unreviewed', 'reviewed')),
                created_at INTEGER NOT NULL,
                archived_at INTEGER,
                archived INTEGER GENERATED ALWAYS AS (archived_at IS NOT NULL) VIRTUAL,
                operation_id TEXT NOT NULL,
                lineage_kind TEXT NOT NULL CHECK (lineage_kind IN ('file', 'video', 'generated')),
                source_family_id TEXT NOT NULL,
                video_name TEXT,
                timestamp_ms INTEGER,
                frame_index INTEGER,
                generation_run_id TEXT,
                sample_index INTEGER,
                recipe_revision INTEGER,
                UNIQUE (group_id, ordinal)
            ) STRICT;
            CREATE INDEX images_archived_page ON images (group_id, archived, ordinal);
            CREATE INDEX images_review_page ON images (group_id, review_state, ordinal);
            CREATE INDEX images_filtered_page ON images (group_id, archived, review_state, ordinal);
            CREATE INDEX images_pixels ON images (group_id, pixel_hash);
            CREATE TABLE image_annotations (
                image_id TEXT NOT NULL REFERENCES images (id) ON DELETE CASCADE,
                revision INTEGER NOT NULL,
                review_state TEXT NOT NULL CHECK (review_state IN ('unreviewed', 'reviewed')),
                roi_x REAL,
                roi_y REAL,
                roi_width REAL,
                roi_height REAL,
                object_count INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                PRIMARY KEY (image_id, revision)
            ) STRICT, WITHOUT ROWID;
            CREATE TABLE image_annotation_objects (
                image_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                position INTEGER NOT NULL,
                object_id TEXT NOT NULL,
                class_id TEXT NOT NULL,
                class_variant_id TEXT,
                x REAL NOT NULL,
                y REAL NOT NULL,
                width REAL NOT NULL,
                height REAL NOT NULL,
                PRIMARY KEY (image_id, revision, position),
                FOREIGN KEY (image_id, revision) REFERENCES image_annotations (image_id, revision) ON DELETE CASCADE
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX image_annotation_objects_class ON image_annotation_objects (class_id);
            CREATE INDEX image_annotation_objects_class_variant ON image_annotation_objects (class_variant_id) WHERE class_variant_id IS NOT NULL;
            CREATE INDEX jobs_image_import_group ON jobs (json_extract(input, '$.groupId'), seq) WHERE kind = 'images.import';
            """),
        // Backgrounds: sets with a fixed target size and ready PNG items. revision changes with the set's fields and archive,
        // content_revision with every change of the composition; item_count counts active items, removed_count removed ones.
        // background_items.ordinal is the order of addition (keyset pages over active, removed or all items); signature is the
        // 32×32 greyscale similarity signature (1024 bytes) and pixel_hash finds exact repeats in the set.
        // jobs_background_import_set lists the preparation history of a set.
        SchemaMigration.Sql(5, """
            CREATE TABLE background_sets (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                target_width INTEGER NOT NULL CHECK (target_width > 0),
                target_height INTEGER NOT NULL CHECK (target_height > 0),
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                archived_at INTEGER,
                revision INTEGER NOT NULL,
                content_revision INTEGER NOT NULL DEFAULT 1,
                item_count INTEGER NOT NULL DEFAULT 0,
                removed_count INTEGER NOT NULL DEFAULT 0,
                next_ordinal INTEGER NOT NULL DEFAULT 1
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX background_sets_created ON background_sets (created_at, id);
            CREATE TABLE background_items (
                id TEXT NOT NULL PRIMARY KEY,
                set_id TEXT NOT NULL REFERENCES background_sets (id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL,
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                byte_size INTEGER NOT NULL CHECK (byte_size > 0),
                sha256 TEXT NOT NULL,
                pixel_hash TEXT NOT NULL,
                signature BLOB NOT NULL CHECK (length(signature) = 1024),
                revision INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                removed_at INTEGER,
                removed INTEGER GENERATED ALWAYS AS (removed_at IS NOT NULL) VIRTUAL,
                operation_id TEXT NOT NULL,
                origin TEXT NOT NULL CHECK (origin IN ('uploaded', 'image-crop', 'video-frame')),
                source_name TEXT NOT NULL,
                source_hash TEXT NOT NULL,
                video_name TEXT,
                timestamp_ms INTEGER,
                frame_index INTEGER,
                crop_left INTEGER,
                crop_top INTEGER,
                blur_radius REAL,
                processor_version TEXT NOT NULL,
                UNIQUE (set_id, ordinal)
            ) STRICT;
            CREATE INDEX background_items_page ON background_items (set_id, removed, ordinal);
            CREATE INDEX background_items_pixels ON background_items (set_id, pixel_hash);
            CREATE INDEX jobs_background_import_set ON jobs (json_extract(input, '$.setId'), seq) WHERE kind = 'backgrounds.import';
            """),
        // Generation: recipes keep their lists as JSON arrays (a handful of IDs; the deletion checks of other sections read
        // them with json_each). A run is the job generation.run; generation_runs adds what the run prepared: the output group
        // and the ordered source manifest of its snapshot (released_at — the snapshot files are gone). A deleted run keeps
        // its row with deleted_at so that its operation never starts again. jobs_generation lists the runs in queue order.
        SchemaMigration.Sql(6, """
            CREATE TABLE generation_recipes (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                background_set_ids TEXT NOT NULL,
                class_variant_ids TEXT NOT NULL,
                variant_weights TEXT NOT NULL,
                objects_per_image INTEGER NOT NULL CHECK (objects_per_image >= 0),
                empty_probability REAL NOT NULL CHECK (empty_probability BETWEEN 0 AND 1),
                scale REAL NOT NULL CHECK (scale > 0),
                revision INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                archived_at INTEGER
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX generation_recipes_created ON generation_recipes (created_at, id);
            CREATE TABLE generation_runs (
                id TEXT NOT NULL PRIMARY KEY,
                operation_id TEXT NOT NULL UNIQUE,
                output_group_id TEXT,
                sources TEXT,
                created_at INTEGER NOT NULL,
                released_at INTEGER,
                deleted_at INTEGER
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX jobs_generation ON jobs (seq) WHERE kind = 'generation.run';
            """),
        // Datasets: a plan stores its summary and hashes, not frames (the build recomputes and compares the hash). A version is
        // the job datasets.build (same ID): building → ready; a deleted version keeps its row as the passport for models
        // (state deleted, samples gone). Samples are rows per version with keyset pages by (split, image_id); position is the
        // build order, resource_id serves the sample file through the resource channel. The job indexes list builds, checks
        // and exports in queue order and find the checks and exports of a version.
        SchemaMigration.Sql(7, """
            CREATE TABLE dataset_plans (
                id TEXT NOT NULL PRIMARY KEY,
                operation_id TEXT NOT NULL UNIQUE,
                specification TEXT NOT NULL,
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                classes TEXT NOT NULL,
                composition TEXT NOT NULL,
                skipped TEXT NOT NULL,
                warnings TEXT NOT NULL,
                sufficient INTEGER NOT NULL CHECK (sufficient IN (0, 1)),
                hash TEXT NOT NULL,
                source_revision TEXT NOT NULL,
                created_at INTEGER NOT NULL
            ) STRICT, WITHOUT ROWID;
            CREATE TABLE dataset_versions (
                id TEXT NOT NULL PRIMARY KEY,
                state TEXT NOT NULL CHECK (state IN ('building', 'ready', 'deleted')),
                plan_id TEXT NOT NULL,
                plan_hash TEXT NOT NULL,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                revision INTEGER NOT NULL,
                archived_at INTEGER,
                integrity_state TEXT NOT NULL CHECK (integrity_state IN ('unverified', 'verified', 'corrupt')),
                verified_at INTEGER,
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                specification TEXT NOT NULL,
                class_map TEXT NOT NULL,
                variants TEXT NOT NULL,
                composition TEXT NOT NULL,
                manifest_hash TEXT NOT NULL,
                data_yaml_hash TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                deleted_at INTEGER
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX dataset_versions_list ON dataset_versions (state, created_at);
            CREATE TABLE dataset_samples (
                version_id TEXT NOT NULL REFERENCES dataset_versions (id) ON DELETE CASCADE,
                split TEXT NOT NULL CHECK (split IN ('train', 'val', 'test')),
                image_id TEXT NOT NULL,
                position INTEGER NOT NULL,
                resource_id TEXT NOT NULL UNIQUE,
                group_id TEXT NOT NULL,
                generated INTEGER NOT NULL CHECK (generated IN (0, 1)),
                source_format TEXT NOT NULL CHECK (source_format IN ('png', 'jpeg', 'webp', 'bmp')),
                source_sha256 TEXT NOT NULL,
                format TEXT CHECK (format IS NULL OR format IN ('png', 'jpeg')),
                width INTEGER NOT NULL CHECK (width > 0),
                height INTEGER NOT NULL CHECK (height > 0),
                pixel_hash TEXT NOT NULL,
                annotation_revision INTEGER NOT NULL,
                roi_x REAL,
                roi_y REAL,
                roi_width REAL,
                roi_height REAL,
                boxes TEXT NOT NULL,
                negatives TEXT NOT NULL,
                byte_size INTEGER,
                image_hash TEXT,
                label_hash TEXT,
                PRIMARY KEY (version_id, split, image_id)
            ) STRICT, WITHOUT ROWID;
            CREATE UNIQUE INDEX dataset_samples_position ON dataset_samples (version_id, position);
            CREATE INDEX jobs_datasets_build ON jobs (seq) WHERE kind = 'datasets.build';
            CREATE INDEX jobs_datasets_verify ON jobs (seq) WHERE kind = 'datasets.verify';
            CREATE INDEX jobs_datasets_export ON jobs (seq) WHERE kind = 'datasets.export';
            CREATE INDEX jobs_datasets_verify_version ON jobs (json_extract(input, '$.versionId'), seq) WHERE kind = 'datasets.verify';
            CREATE INDEX jobs_datasets_export_version ON jobs (json_extract(input, '$.versionId'), seq) WHERE kind = 'datasets.export';
            """),
        // Training: a run is the job training.run (same ID); training_runs adds what it produced while running — phase, the live
        // progress, effective parameters and environment of the worker, its error with the worker's message, the resulting
        // version — and counters of its history. Epochs and log lines are rows per run (keyset pages by epoch and seq). A model
        // version keeps its passport (dataset reference, parameters, classes in output order, metrics, environment) and its files
        // under files/models/<versionId>/ with the hash and a resource ID; one version at most is the working one.
        SchemaMigration.Sql(8, """
            CREATE TABLE training_runs (
                id TEXT NOT NULL PRIMARY KEY,
                phase TEXT NOT NULL CHECK (phase IN ('preparing', 'training', 'finished')),
                stage TEXT,
                epoch INTEGER,
                epochs INTEGER,
                batch INTEGER,
                batches INTEGER,
                metrics TEXT,
                effective_params TEXT,
                environment TEXT,
                error_code TEXT,
                error_message TEXT,
                result_version_id TEXT,
                epoch_count INTEGER NOT NULL DEFAULT 0,
                log_count INTEGER NOT NULL DEFAULT 0,
                revision INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX training_runs_active ON training_runs (phase) WHERE phase <> 'finished';
            CREATE TABLE training_epochs (
                run_id TEXT NOT NULL REFERENCES training_runs (id) ON DELETE CASCADE,
                epoch INTEGER NOT NULL,
                losses TEXT NOT NULL,
                metrics TEXT NOT NULL,
                lr REAL,
                fitness REAL,
                PRIMARY KEY (run_id, epoch)
            ) STRICT, WITHOUT ROWID;
            CREATE TABLE training_log (
                run_id TEXT NOT NULL REFERENCES training_runs (id) ON DELETE CASCADE,
                seq INTEGER NOT NULL,
                at INTEGER NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('log', 'stage', 'warning', 'system')),
                message TEXT NOT NULL,
                PRIMARY KEY (run_id, seq)
            ) STRICT, WITHOUT ROWID;
            CREATE TABLE model_versions (
                id TEXT NOT NULL PRIMARY KEY,
                number INTEGER NOT NULL UNIQUE,
                run_id TEXT NOT NULL UNIQUE,
                dataset TEXT NOT NULL,
                architecture TEXT NOT NULL,
                input_width INTEGER NOT NULL CHECK (input_width > 0),
                input_height INTEGER NOT NULL CHECK (input_height > 0),
                image_size INTEGER NOT NULL CHECK (image_size > 0),
                parameters TEXT NOT NULL,
                effective_params TEXT NOT NULL,
                epochs_completed INTEGER NOT NULL,
                stopped_early INTEGER NOT NULL CHECK (stopped_early IN (0, 1)),
                best_epoch INTEGER,
                classes TEXT NOT NULL,
                metrics TEXT NOT NULL,
                notes TEXT NOT NULL,
                environment TEXT NOT NULL,
                working INTEGER NOT NULL DEFAULT 0 CHECK (working IN (0, 1)),
                revision INTEGER NOT NULL,
                created_at INTEGER NOT NULL
            ) STRICT, WITHOUT ROWID;
            CREATE UNIQUE INDEX model_versions_working ON model_versions (working) WHERE working = 1;
            CREATE TABLE model_version_files (
                version_id TEXT NOT NULL REFERENCES model_versions (id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('weights_pt', 'onnx', 'plot', 'log')),
                byte_size INTEGER NOT NULL CHECK (byte_size >= 0),
                sha256 TEXT NOT NULL,
                resource_id TEXT NOT NULL UNIQUE,
                PRIMARY KEY (version_id, name)
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX jobs_training ON jobs (seq) WHERE kind = 'training.run';
            """),
        // Checks: an evaluation of a model version on a dataset part is the job checks.evaluate (same ID); model_evaluations keeps
        // what it produced — phase, metrics of the whole part and per class, the image count, environment and error. The row goes
        // with its version (ON DELETE CASCADE). The partial index finds the latest successful evaluation of each version on a
        // dataset part for the comparison matrix; the job indexes list evaluations newest first and by version.
        SchemaMigration.Sql(9, """
            CREATE TABLE model_evaluations (
                id TEXT NOT NULL PRIMARY KEY,
                version_id TEXT NOT NULL REFERENCES model_versions (id) ON DELETE CASCADE,
                dataset_version_id TEXT NOT NULL,
                split TEXT NOT NULL CHECK (split IN ('val', 'test')),
                phase TEXT NOT NULL CHECK (phase IN ('preparing', 'evaluating', 'finished')),
                succeeded INTEGER NOT NULL DEFAULT 0 CHECK (succeeded IN (0, 1)),
                metrics TEXT,
                class_metrics TEXT,
                image_count INTEGER,
                environment TEXT,
                error_code TEXT,
                error_message TEXT,
                revision INTEGER NOT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                finished_at INTEGER
            ) STRICT, WITHOUT ROWID;
            CREATE INDEX model_evaluations_latest ON model_evaluations (dataset_version_id, split, version_id, finished_at) WHERE succeeded = 1;
            CREATE INDEX model_evaluations_active ON model_evaluations (phase) WHERE phase <> 'finished';
            CREATE INDEX model_evaluations_version ON model_evaluations (version_id);
            CREATE INDEX jobs_checks_evaluate ON jobs (seq) WHERE kind = 'checks.evaluate';
            CREATE INDEX jobs_checks_evaluate_version ON jobs (json_extract(input, '$.versionId'), seq) WHERE kind = 'checks.evaluate';
            """),
        // Overview: a job keeps the label its enqueuer gave (a group, dataset or version name; null for older jobs). jobs_active holds
        // only queued and running jobs, so their page in queue order and their exact count by kind do not depend on the history size.
        SchemaMigration.Sql(10, """
            ALTER TABLE jobs ADD COLUMN label TEXT;
            CREATE INDEX jobs_active ON jobs (seq, kind) WHERE status IN ('queued', 'running');
            """),
    ];

    internal static int CurrentVersion => Migrations[^1].Version;
}
