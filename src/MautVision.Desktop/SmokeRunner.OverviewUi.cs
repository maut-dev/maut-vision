using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Desktop.Controllers.Hardware;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// «Обзор» and «Аналитика» from the real page with the fake worker, on the project the other checks filled: the header, the counters
    /// against overview.get, the latest models and their passports, the links; «Ваш компьютер» against hardware.sample, a missing GPU and
    /// a failed sample through the smoke seam of the host; 32 training runs and an evaluation in the list of current jobs by events only
    /// (labels, «Ожидает видеокарту», pages of 30, «Открыть» into the run and into «Проверки → Датасет»); the analytics empty state,
    /// the default and saved choice of versions, the table with the best values and gaps, «Копировать таблицу», the curves and the
    /// evaluations of the chosen versions. The page ends on «Аналитика» for the preview capture.
    /// </summary>
    private static async Task CheckOverviewUiAsync(CoreWebView2 web, string projectId, JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        const string missing = "NVIDIA GPU не обнаружена. Для других производителей телеметрия пока недоступна.";
        var steps = new (string Body, Action Prepare)[]
        {
            (OverviewUiScript, () => HardwareController.SmokeOverride = null),
            (OverviewUiMissingGpuScript, () => HardwareController.SmokeOverride = sample => sample with
            {
                Gpus = [], GpuStatus = "unavailable", GpuReason = "hardware.gpu_not_found", GpuSource = null,
            }),
            (OverviewUiFailedSampleScript, () => HardwareController.SmokeOverride = _ => throw new InvalidOperationException("Smoke: the sample fails.")),
            (OverviewUiJobsScript, () => HardwareController.SmokeOverride = null),
            (AnalyticsUiScript, () => { }),
        };
        var arguments = JsonSerializer.Serialize(new { projectId, missing });
        try
        {
            foreach (var (body, prepare) in steps)
            {
                prepare();
                var script = "(async ({projectId, missing}) => {" + BackgroundsUiHelpers + TrainingUiHelpers + ChecksUiHelpers + OverviewUiHelpers + body + "})(" + arguments + ")";
                JsonNode result;
                try
                {
                    result = await RunScriptAsync(web, script, token, attempts: 1200);
                }
                catch (TimeoutException error)
                {
                    var passed = await web.ExecuteScriptAsync("JSON.stringify(window.__trainingUiChecks ?? [])");
                    throw new TimeoutException(error.Message + " Passed: " + JsonSerializer.Deserialize<string>(passed), error);
                }
                foreach (var check in result["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
            }
        }
        finally
        {
            HardwareController.SmokeOverride = null;
        }
    }

    private const string OverviewUiHelpers = """
          const number = id => Number(text('[data-testid="' + id + '"]').replace(/\s/g, ''));
          const metric = value => typeof value === 'number' ? value.toFixed(3) : '—';
          const openOverview = async () => {
            if (!$('[data-testid="overview-section"]')) section('overview').click();
            return wait(() => $('[data-testid="overview-ready-datasets"]') && $('[data-testid="overview-section"]'), 'Overview data');
          };
          const jobRows = () => $$('[data-testid="overview-job"]');
          const jobRow = id => $('[data-testid="overview-job"][data-job-id="' + id + '"]');
          const jobState = row => $('[data-testid="overview-job-state"]', row)?.textContent ?? '';
          const jobDone = async jobId => {
            for (let i = 0; i < 300; i++) {
              const job = await request('jobs.get', {projectId, jobId});
              if (job.ok && !['queued', 'running'].includes(job.data.status)) return job.data;
              await sleep(100);
            }
            throw new Error('Job did not finish: ' + jobId);
          };

        """;

    private const string OverviewUiScript = """
          await openProject(projectId);
          await openOverview();
          const project = (await request('projects.list', {})).data.items.find(item => item.id === projectId);
          assert(document.body.textContent.includes('Обзор · Данные проекта, текущие задачи, модели и нагрузка компьютера.')
            && text('[data-testid="overview-architecture"]') === project.baseModelId + ' · ' + project.inputSize.width + ' × ' + project.inputSize.height + ' px'
            && text('[data-testid="overview-description"]') === (project.description || 'Укажите задачу распознавания в настройках проекта.')
            && !document.body.textContent.includes('Материалы проекта') && !$$('[data-testid="overview-section"] button').some(item => item.textContent === 'Обновить'),
            'The overview opens with the project, its architecture and input, without «Материалы проекта» and «Обновить»');
          $('[data-testid="overview-edit"]').click();
          const editor = await wait(() => $('[role="dialog"]'), 'Project editor');
          key(editor, 'Escape');
          await wait(() => !$('[role="dialog"]'), 'Editor closed');
          assert(true, '«Настройки проекта» opens the project editor');

          const overview = (await request('overview.get', {projectId})).data;
          await wait(() => number('overview-active-total') === overview.activeJobs.total, 'Counters', () => text('[data-testid="overview-data"]'));
          assert(number('overview-ready-datasets') === overview.datasets.ready && number('overview-model-versions') === overview.modelVersionCount
            && text('[data-testid="overview-dataset-states"]') === 'Архивных датасетов: ' + overview.datasets.archived + ' · Повреждённых: ' + overview.datasets.corrupt,
            '«Данные проекта» count ready datasets, model versions and current jobs as overview.get: ' + text('[data-testid="overview-data"]'));
          if (overview.activeJobs.total === 0) {
            assert(text('[data-testid="overview-jobs-empty"]') === 'Активных задач нет. Можно подготовить изображения, собрать датасет или запустить обучение.',
              'Without active jobs «Сейчас выполняется» suggests what to do');
          }
          const latest = overview.latestVersions;
          const rows = $$('[data-testid="overview-model"]');
          assert(latest.length > 0 && rows.map(item => item.dataset.versionId).join() === latest.map(item => item.id).join()
            && latest.every((version, index) => $('h3', rows[index]).textContent === 'Версия ' + version.number + ' · ' + version.architecture
              && !!$('[data-testid="overview-model-working"]', rows[index]) === version.isWorking
              && $('[data-testid="overview-model-metrics"]', rows[index]).textContent === 'mAP50 ' + metric(version.metrics.mAP50) + ' · mAP50–95 ' + metric(version.metrics.mAP50_95)),
            '«Последние модели» list up to 5 versions newest first with «Рабочая» and mAP50 · mAP50–95');
          $('[data-testid="overview-model-passport"]', rows[0]).click();
          await wait(() => $('[data-testid="model-version-passport"]')?.dataset.versionId === latest[0].id && section('models').getAttribute('aria-current') === 'page',
            'Passport from the overview');
          assert(true, '«Паспорт» opens the version passport in «Модели»');
          await openOverview();
          $('[data-testid="overview-all-models"]').click();
          await wait(() => $('[data-testid="models-list"]') && section('models').getAttribute('aria-current') === 'page', 'All models');
          assert(true, '«Все модели» opens the list of «Модели»');
          await openOverview();
          $('[data-testid="overview-open-datasets"]').click();
          await wait(() => $('[data-testid="datasets-section"]') && section('datasets').getAttribute('aria-current') === 'page', 'Datasets');
          assert(true, '«Открыть датасеты» opens «Датасеты»');

          await openOverview();
          const hardware = await wait(() => $('[data-testid="overview-cpu"]') && $('[data-testid="overview-hardware"]'), 'Hardware sample');
          const first = $('[data-testid="overview-cpu"]').dataset.status;
          if (first === 'collecting') {
            assert(text('[data-testid="overview-cpu-load-value"]') === 'Недоступно' && text('[data-testid="overview-cpu-load-detail"]') === 'Собирается первый замер нагрузки.',
              'Before the second sample the CPU load says «Недоступно» and «Собирается первый замер нагрузки.»');
          }
          await wait(() => $('[data-testid="overview-cpu"]').dataset.status === 'available' && Number($('[data-testid="overview-load-chart"]').dataset.points) >= 2,
            'CPU load and two samples', () => $('[data-testid="overview-load-chart"]')?.dataset.points);
          const sample = (await request('hardware.sample', {})).data;
          assert(/^\d+(,\d)?%$/.test(text('[data-testid="overview-cpu-load-value"]')) && $('[data-testid="overview-cpu-load"] [role="progressbar"]')
            && text('[data-testid="overview-cpu-threads"]').endsWith(russianPlural(sample.cpu.logicalThreads, 'поток', 'потока', 'потоков'))
            && $('h3', $('[data-testid="overview-cpu"]')).textContent === (sample.cpu.model ?? 'Процессор'),
            'CPU shows its model, threads and the load in percent: ' + text('[data-testid="overview-cpu"]'));
          assert(/^\d+(,\d{1,2})? ГБ занято из \d+(,\d{1,2})? ГБ$/.test(text('[data-testid="overview-ram-detail"]')) && /^\d+(,\d)?%$/.test(text('[data-testid="overview-ram-value"]')),
            'RAM shows the percent and «X ГБ занято из Y ГБ»: ' + text('[data-testid="overview-ram"]'));
          const parts = text('[data-testid="overview-application-parts"]');
          assert(/^Приложение: \d+(,\d{1,2})? ГБ/.test(text('[data-testid="overview-application"]')) && parts.startsWith('Основной процесс ')
            && parts.includes(' · WebView2 (') && parts.includes(' · Обучение и проверки ('), 'The memory of the application has its parts: ' + parts);
          if (sample.gpuStatus === 'available') {
            const gpus = $$('[data-testid="overview-gpu"]');
            assert(gpus.length === sample.gpus.length && sample.gpus.every((gpu, index) => $('h3', gpus[index]).textContent === 'GPU ' + gpu.index + ' · ' + gpu.name
              && $('[data-testid="overview-gpu-details"]', gpus[index]).textContent.startsWith('Температура ')
              && $('[data-testid="overview-gpu-details"]', gpus[index]).textContent.endsWith('Драйвер ' + (gpu.driverVersion ?? 'Недоступно'))),
              'Every GPU shows its name, load, memory, temperature and driver: ' + text('[data-testid="overview-gpus"]'));
            await wait(() => $('[data-load-line="gpu-' + sample.gpus[0].index + '"]'), 'GPU load line');
          } else {
            assert(text('[data-testid="overview-gpu-reason"]') === mautLocale.errorText(sample.gpuReason), 'Without a GPU the reason of the host is shown: ' + sample.gpuReason);
          }
          assert($('[data-load-line="cpu"]') && text('[data-testid="overview-load-chart"] figcaption').startsWith('Нагрузка за 2 минуты'),
            'The mini chart draws the CPU and GPU load of the last minutes');
          assert(/^Замер: \d{1,2}:\d{2}:\d{2}$/.test(text('[data-testid="overview-hardware-time"]')), '«Замер» names the time of the sample');
          return {checks};
        """;

    private const string OverviewUiMissingGpuScript = """
          await openOverview();
          await wait(() => text('[data-testid="overview-gpu-reason"]') === missing && $('[data-testid="overview-gpus"]').dataset.status === 'unavailable'
            && !$('[data-testid="overview-gpu"]'), 'Missing GPU', () => text('[data-testid="overview-gpus"]'));
          assert(true, 'A missing NVIDIA GPU shows the reason of the host: ' + missing);
          return {checks};
        """;

    private const string OverviewUiFailedSampleScript = """
          await openOverview();
          await wait(() => text('[data-testid="overview-hardware-error"]') === 'Не удалось обновить показатели компьютера. Показатели могут быть устаревшими.'
            && $('[data-testid="overview-cpu"]'), 'Failed sample');
          assert(true, 'A failed sample keeps the last values and says they may be outdated');
          return {checks};
        """;

    private const string OverviewUiJobsScript = """
          await openOverview();
          await wait(() => !$('[data-testid="overview-hardware-error"]'), 'Sample after the failure');
          assert(true, 'The next sample clears the error');
          const dataset = (await request('datasets.list', {projectId})).data.find(item => item.name === 'Проверка smoke');
          assert(dataset && number('overview-active-total') === 0, 'The dataset of the checks is left and nothing runs');
          await rename(dataset.id, 'fake:hang');
          const queue = [];
          for (let index = 0; index < 32; index++) {
            const started = await request('training.startRun', {projectId, datasetVersionId: dataset.id, parameters: {epochs: 2, batch: 4, device: 'cpu'}, presetName: null,
              operationId: 'overview-ui-run-' + index});
            if (!started.ok) throw new Error('Run ' + index + ': ' + JSON.stringify(started.error));
            queue.push(started.data.id);
          }
          await wait(() => number('overview-active-total') === 32 && jobRows().length === 30, 'Active jobs', () => text('[data-testid="overview-active-total"]') + ' / ' + jobRows().length);
          assert(jobRows().map(item => item.dataset.jobId).join() === queue.slice(0, 30).join()
            && jobRows().every(item => $('[data-testid="overview-job-title"]', item).textContent === 'Обучение · fake:hang'),
            'job.changed alone brings the runs into «Сейчас выполняется» in queue order with «Обучение · датасет», 30 per page');
          await wait(() => jobRows()[0].dataset.status === 'running' && jobState(jobRows()[0]).startsWith('Эпоха '), 'Running state', () => jobState(jobRows()[0]));
          assert(jobRows().slice(1).every(item => item.dataset.status === 'queued' && jobState(item) === 'Ожидает видеокарту'),
            'The running job shows its progress text, the queued GPU jobs «Ожидает видеокарту»: ' + jobState(jobRows()[0]));
          $('[data-testid="overview-jobs-more"]').click();
          await wait(() => jobRows().length === 32 && !$('[data-testid="overview-jobs-more"]'), 'Second page');
          assert(jobRows().slice(30).map(item => item.dataset.jobId).join() === queue.slice(30).join(), '«Показать ещё задачи» loads the next page');

          $('[data-testid="overview-job-open"]', jobRow(queue[31])).click();
          await wait(() => section('training').getAttribute('aria-current') === 'page' && selectedTab() === 'training-tab-current'
            && passport()?.dataset.runId === queue[31], 'Run from the overview', () => passport()?.dataset.runId);
          assert(true, '«Открыть» of a training job opens «Текущий запуск» with that run');
          for (const runId of queue.slice(1).reverse()) {
            const cancelled = await request('training.cancelRun', {projectId, runId});
            if (!cancelled.ok) throw new Error('Cancel ' + runId + ': ' + JSON.stringify(cancelled.error));
          }

          await openOverview();
          await wait(() => number('overview-active-total') === 1 && jobRows().length === 1, 'Queued runs cancelled', () => text('[data-testid="overview-active-total"]'));
          await rename(dataset.id, 'Проверка smoke');
          const working = (await modelVersions()).find(version => version.isWorking);
          const evaluation = await request('checks.startEvaluation', {projectId, versionId: working.id, datasetVersionId: dataset.id, split: 'val', device: 'cpu',
            batch: 4, operationId: 'overview-ui-evaluation'});
          if (!evaluation.ok) throw new Error('Evaluation: ' + JSON.stringify(evaluation.error));
          const evaluationRow = await wait(() => jobRow(evaluation.data.job.id), 'Evaluation row');
          assert($('[data-testid="overview-job-title"]', evaluationRow).textContent === 'Проверка модели · Версия ' + working.number + ' · Проверка smoke'
            && jobState(evaluationRow) === 'Ожидает видеокарту' && number('overview-active-total') === 2,
            'An evaluation behind the training waits for the GPU: ' + $('[data-testid="overview-job-title"]', evaluationRow).textContent);
          $('[data-testid="overview-job-open"]', evaluationRow).click();
          await wait(() => section('checks').getAttribute('aria-current') === 'page' && modeTab('dataset')?.getAttribute('aria-selected') === 'true'
            && $('[data-testid="model-evaluations"]'), 'Evaluation from the overview');
          assert(true, '«Открыть» of an evaluation opens «Проверки» in the «Датасет» mode');
          const cancelEvaluation = await request('checks.cancelEvaluation', {projectId, evaluationId: evaluation.data.id});
          const cancelRun = await request('training.cancelRun', {projectId, runId: queue[0]});
          assert(cancelEvaluation.ok && cancelRun.ok, 'The evaluation and the hanging run are cancelled');
          await openOverview();
          await wait(() => number('overview-active-total') === 0 && $('[data-testid="overview-jobs-empty"]'), 'No active jobs', () => text('[data-testid="overview-jobs"]'));
          assert(true, 'Finished jobs leave «Сейчас выполняется» by events');
          await jobDone(queue[0]);
          return {checks};
        """;

    private const string AnalyticsUiScript = """
          const blank = (await request('projects.list', {})).data.items.find(item => item.name === 'Модели без версий' && !item.archivedAt);
          assert(blank, 'The project without versions is left by the checks');
          await openProject(blank.id);
          await openSection('analytics', 'analytics-empty');
          assert(text('[data-testid="analytics-empty"]').startsWith('Сравнивать пока нечего')
            && text('[data-testid="analytics-empty"]').includes('Аналитика сравнивает версии модели проекта: метрики, кривые обучения и проверки на датасетах. Версия появляется после успешного обучения.'),
            '«Аналитика» without versions says «Сравнивать пока нечего»');
          $('[data-testid="analytics-open-training"]').click();
          await wait(() => $('[data-testid="training-section"]') && section('training').getAttribute('aria-current') === 'page', 'Training from analytics');
          assert(true, '«Открыть обучение» opens «Обучение»');
          await openProject(projectId);

          const dataset = (await request('datasets.list', {projectId})).data.find(item => item.name === 'Проверка smoke');
          await rename(dataset.id, 'fake:better');
          const better = await request('training.startRun', {projectId, datasetVersionId: dataset.id, parameters: {epochs: 3, batch: 4, device: 'cpu'}, presetName: null,
            operationId: 'analytics-ui-better'});
          const trained = await jobDone(better.data.id);
          await rename(dataset.id, 'Проверка smoke');
          assert(trained.status === 'succeeded' && trained.result?.versionId, 'A version with other metrics is trained for the comparison');
          const versions = await modelVersions();
          const storageKey = 'maut.analytics.selection.' + projectId;
          localStorage.removeItem(storageKey);
          await openSection('analytics', 'analytics-section');
          await wait(() => $('[data-testid="analytics-table"]'), 'Analytics table');
          assert(document.body.textContent.includes('Аналитика · Сравнение версий модели: метрики, кривые обучения и проверки на датасетах.'),
            '«Аналитика» opens lazily with its description');
          const boxes = $$('[data-testid="analytics-version"]');
          const chosen = versions.slice(0, 3);
          assert(boxes.map(item => item.dataset.versionId).join() === versions.map(item => item.id).join()
            && boxes.filter(item => item.dataset.state === 'checked').map(item => item.dataset.versionId).join() === chosen.map(item => item.id).join(),
            'All versions are listed newest first; the 3 latest are chosen by default');
          const columns = () => $$('[data-testid="analytics-table"] thead th[data-version-id]').map(item => item.dataset.versionId);
          assert(columns().join() === chosen.map(item => item.id).join(), 'The table has a column per chosen version');
          const cells = key => $$('[data-testid="analytics-table"] tr[data-row="' + key + '"] td[data-version-id]');
          for (const key of ['mAP50', 'mAP50_95', 'precision', 'recall']) {
            const best = metricBest(chosen, key);
            const marked = cells(key).filter(item => item.dataset.best);
            const gaps = cells(key).map(item => $('[data-testid="analytics-gap"]', item)?.textContent ?? null);
            assert(best !== null && marked.length === chosen.filter(version => version.metrics[key] === best).length
              && marked.every(item => item.textContent === best.toFixed(3))
              && chosen.every((version, index) => version.metrics[key] === best ? gaps[index] === null : gaps[index] === '−' + (best - version.metrics[key]).toFixed(3)),
              'Metric ' + key + ' marks the best value and the gap of the others');
          }
          const datasets = (await request('datasets.list', {projectId, includeArchived: true})).data;
          const datasetText = version => {
            const found = datasets.find(item => item.id === version.dataset.versionId);
            return !found ? 'Датасет удалён' : found.archivedAt ? found.name + ' (в архиве)' : found.name;
          };
          assert(chosen.every((version, index) => cells('bestEpoch')[index].textContent === (version.bestEpoch === null ? '—' : String(version.bestEpoch))
              && cells('epochs')[index].textContent === version.epochsCompleted + ' / ' + version.parameters.epochs
              && cells('dataset')[index].textContent === datasetText(version)
              && cells('split')[index].textContent === version.dataset.counts.train + ' / ' + version.dataset.counts.val
              && cells('device')[index].textContent === (version.parameters.device === 'cpu' ? 'процессор' : 'видеокарта ' + version.parameters.device)
              && cells('lr0')[index].textContent.endsWith('(подобрано)') === ['computed', 'optimizer-auto'].includes(version.effectiveParams.lr0Source)),
            'Rows show the best epoch, epochs, dataset, train / val frames, device and «(подобрано)» lr0');
          assert($('[data-testid="analytics-table"] tr[data-row="mAP50"] td').title.startsWith('mAP50 — средняя точность по классам при IoU ≥ 0.5')
            && $('[data-testid="analytics-table"] tr[data-row="bestEpoch"] td').title.startsWith('Лучшая эпоха — эпоха с максимальным fitness'), 'Row names carry the help');

          let copied;
          navigator.clipboard.writeText = async value => { copied = value; };
          try {
            $('[data-testid="analytics-copy"]').click();
            await wait(() => copied && text('[data-testid="analytics-copy"]') === 'Скопировано', 'Copied');
          } finally {
            delete navigator.clipboard.writeText;
          }
          const lines = copied.split('\n');
          assert(lines.length === 14 && lines[0] === ['Параметр', ...chosen.map(version => 'Версия ' + version.number)].join('\t')
            && lines[1] === ['mAP50', ...chosen.map(version => metric(version.metrics.mAP50))].join('\t') && lines.every(line => line.split('\t').length === chosen.length + 1),
            '«Копировать таблицу» copies TSV and says «Скопировано»: ' + JSON.stringify(lines.slice(0, 2)));
          await wait(() => text('[data-testid="analytics-copy"]') === 'Копировать таблицу', 'Copy label back');
          assert(true, '«Скопировано» returns to «Копировать таблицу» after 2 s');

          const curves = await wait(() => $('[data-chart="analytics"]'), 'Curves', () => text('[data-testid="analytics-curves"]'));
          const histories = await Promise.all(chosen.map(version => request('training.listEpochs', {projectId, runId: version.runId, limit: 1000})));
          const drawn = chosen.filter((version, index) => histories[index].data.items.some(entry => typeof entry.metrics.mAP50 === 'number'));
          assert($('[data-testid="analytics-curves"]').dataset.metric === 'mAP50' && drawn.length > 0
            && drawn.every(version => $('[data-series-line="' + version.id + '"]', curves))
            && $$('[data-series]', curves).map(item => item.textContent).join() === drawn.map(version => 'Версия ' + version.number).join(),
            'The curves draw mAP50 of every chosen version with epochs in its colour');
          const silent = chosen.filter(version => !drawn.includes(version));
          assert(silent.every(version => $('[data-testid="analytics-curves-missing"] [data-version-id="' + version.id + '"]')?.textContent.endsWith('— работник не прислал эпох')),
            'A version without epochs is named in the legend');
          const svg = $('svg[role="img"]', curves);
          const box = svg.getBoundingClientRect();
          svg.dispatchEvent(new PointerEvent('pointermove', {clientX: box.left + box.width * 0.6, clientY: box.top + box.height / 2, bubbles: true}));
          const tooltip = await wait(() => $('[data-testid="training-chart-tooltip"]', curves), 'Tooltip');
          assert(/^Эпоха \d+/.test(tooltip.textContent) && drawn.every(version => tooltip.textContent.includes('Версия ' + version.number)), 'Hover shows «Эпоха N» and the versions');
          svg.dispatchEvent(new PointerEvent('pointerout', {bubbles: true, relatedTarget: document.body}));
          await pick($('[data-testid="analytics-curve-metric"]'), 'Потери классов');
          await wait(() => $('[data-testid="analytics-curves"]').dataset.metric === 'cls_loss', 'Metric changed');
          assert($$('[role="option"]').length === 0 && text('[data-chart="analytics"] figcaption') === 'Потери классов по эпохам', '«Метрика графика» switches the curve');

          for (const box of $$('[data-testid="analytics-version"]').filter(item => item.dataset.state === 'checked')) box.click();
          await wait(() => text('[data-testid="analytics-none"]') === 'Отметьте одну или несколько версий, чтобы сравнить их.', 'Nothing chosen');
          assert(!$('[data-testid="analytics-table"]'), 'Without chosen versions the comparison asks to choose');
          const oldest = versions[versions.length - 1];
          $('[data-testid="analytics-version"][data-version-id="' + oldest.id + '"]').click();
          $('[data-testid="analytics-version"][data-version-id="' + versions[0].id + '"]').click();
          await wait(() => columns().join() === [versions[0].id, oldest.id].join(), 'Two chosen');
          const matrix = await wait(() => $('[data-testid="evaluation-matrix"]'), 'Matrix');
          assert($$('thead th[data-version-id]', matrix).map(item => item.dataset.versionId).join() === [versions[0].id, oldest.id].join()
            && text('[data-testid="evaluation-version"]').startsWith('Версия ' + versions[0].number), '«Проверки на датасетах» compare the chosen versions; the form takes the first');

          await openOverview();
          await openSection('analytics', 'analytics-section');
          await wait(() => columns().join() === [versions[0].id, oldest.id].join(), 'Saved choice');
          assert(JSON.parse(localStorage.getItem(storageKey)).join() === [oldest.id, versions[0].id].join(), 'The choice of versions is kept for the project');
          localStorage.setItem(storageKey, JSON.stringify(['00000000-0000-0000-0000-000000000000', oldest.id]));
          await openOverview();
          await openSection('analytics', 'analytics-section');
          await wait(() => columns().join() === oldest.id && JSON.parse(localStorage.getItem(storageKey)).join() === oldest.id, 'Deleted version dropped');
          assert(true, 'A deleted version leaves the saved choice');
          $('[data-testid="analytics-column"]').click();
          await wait(() => $('[data-testid="model-version-passport"]')?.dataset.versionId === oldest.id, 'Passport from the column');
          assert(true, 'The column header opens the passport of its version');
          localStorage.removeItem(storageKey);
          await openSection('analytics', 'analytics-section');
          await wait(() => $('[data-chart="analytics"] polyline'), 'Analytics for the capture');
          return {checks};
        """;
}
