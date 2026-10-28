using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// «Модели» and «Проверки» from the real page with the fake worker, on the versions and the evaluation the API checks left: the
    /// version list (order, working badge, «лучшая»), the ZIP export plate, the refusal to delete the working version, «Сделать
    /// рабочей», the passport with «← К списку моделей»; the checks shell and its tabs, an image check with boxes over the preview,
    /// the immediate refusal while the GPU is busy (tabs keep the running check), stop; a video check with the crop frame, «Показать
    /// другой кадр», the step estimate, the journal and the reconnection after leaving the section, a stopped video; a screen check
    /// with live counters, the 40 latest frames and the stop when the section is left; evaluations with compatibility hints, the
    /// comparison matrix, per-class metrics and cancel; deleting a version and «Версия удалена» in the training history.
    /// </summary>
    private static async Task CheckChecksUiAsync(CoreWebView2 web, string root, string projectId, JsonNode report, CancellationToken token)
    {
        var checks = report["checks"]!.AsArray();
        var directory = Path.Combine(root, "smoke-checks-ui");
        Directory.CreateDirectory(directory);
        var image = Path.Combine(directory, "кадр.jpg");
        await File.WriteAllBytesAsync(image, Photo(64, 48, 92), token);
        var hang = Path.Combine(directory, "fake-hang.png");
        await File.WriteAllBytesAsync(hang, Photo(32, 32, 93), token);
        var video = Path.Combine(root, "smoke-video", "улица.mp4");
        var hangVideo = Path.Combine(directory, "fake-hang.mp4");
        File.Copy(video, hangVideo, overwrite: true);
        var zip = Path.Combine(directory, "model-ui.zip");

        var steps = new (string Body, Action Prepare)[]
        {
            (ChecksUiModelsScript, () => MainWindow.SmokeSaveAnswer = zip),
            (ChecksUiImageScript, () => MainWindow.SmokeFilesAnswer = [image]),
            (ChecksUiBusyScript, () => MainWindow.SmokeFilesAnswer = [hang]),
            (ChecksUiVideoScript, () => MainWindow.SmokeVideoAnswer = video),
            (ChecksUiVideoStopScript, () => MainWindow.SmokeVideoAnswer = hangVideo),
            (ChecksUiScreenScript, () => { }),
            (ChecksUiDatasetScript, () => { }),
            (ChecksUiDeleteScript, () => { }),
        };
        var arguments = JsonSerializer.Serialize(new { projectId });
        foreach (var (body, prepare) in steps)
        {
            prepare();
            var script = "(async ({projectId}) => {" + BackgroundsUiHelpers + TrainingUiHelpers + ChecksUiHelpers + body + "})(" + arguments + ")";
            JsonNode result;
            try
            {
                result = await RunScriptAsync(web, script, token, attempts: 900);
            }
            catch (TimeoutException error)
            {
                var passed = await web.ExecuteScriptAsync("JSON.stringify(window.__trainingUiChecks ?? [])");
                throw new TimeoutException(error.Message + " Passed: " + JsonSerializer.Deserialize<string>(passed), error);
            }
            foreach (var check in result["checks"]!.AsArray()) checks.Add(check!.GetValue<string>());
            if (body == ChecksUiModelsScript)
            {
                if (!File.Exists(zip) || new FileInfo(zip).Length == 0) throw new InvalidOperationException("«Выгрузить ZIP» did not write the chosen file.");
                checks.Add("«Выгрузить ZIP» writes the archive to the file chosen in «Сохранить как»");
            }
        }
    }

    private const string ChecksUiHelpers = """
          const section = id => $('[data-section="' + id + '"]');
          const openSection = async (id, testId) => {
            (await wait(() => enabled(section(id)), 'Section button ' + id)).click();
            return wait(() => $('[data-testid="' + testId + '"]'), 'Section ' + id);
          };
          const modelVersions = async () => (await request('training.listModelVersions', {projectId})).data.sort((left, right) => right.number - left.number);
          const card = id => $('[data-testid="model-version-card"][data-version-id="' + id + '"]');
          const modeTab = id => $('[data-testid="checks-tab-' + id + '"]');
          const panel = id => $('#checks-panel-' + id);
          const state = prefix => $('[data-testid="' + prefix + '-state"]')?.dataset.state;
          const loaded = image => image && image.complete && image.naturalWidth > 0 ? image : null;
          const openChecks = async () => {
            if (!$('[data-testid="checks-section"]')) await openSection('checks', 'checks-section');
            return $('[data-testid="checks-section"]');
          };
          const metricBest = (versions, key) => {
            const numbers = versions.map(version => version.metrics[key]).filter(value => typeof value === 'number');
            if (numbers.length < 2) return null;
            const best = Math.max(...numbers);
            return numbers.every(value => value === best) ? null : best;
          };

        """;

    private const string ChecksUiModelsScript = """
          const blank = await request('projects.create', {name: 'Модели без версий', description: '', baseModelId: 'yolo26n', inputSize: {width: 320, height: 256}});
          assert(blank.ok, 'A project without versions is created for the empty states');
          await openProject(blank.data.id);
          await openSection('checks', 'checks-empty');
          assert(text('[data-testid="checks-empty"]').startsWith('Проверять пока нечего') && text('[data-testid="checks-empty"]').includes('Версия появляется после успешного обучения.'),
            '«Проверки» without versions say «Проверять пока нечего»');
          $('[data-testid="checks-open-models"]').click();
          const empty = await wait(() => $('[data-testid="models-empty"]'), 'Models empty');
          assert(section('models').getAttribute('aria-current') === 'page' && empty.textContent.startsWith('Моделей пока нет. Версия модели появляется после первого успешного обучения'),
            '«Открыть модели» leads to the empty «Модели»');
          $('[data-testid="models-open-training"]').click();
          await wait(() => $('[data-testid="training-section"]') && section('training').getAttribute('aria-current') === 'page', 'Training opened');
          assert(true, '«К обучению» opens the training section');
          await openProject(projectId);
          const versions = await modelVersions();
          const working = versions.find(version => version.isWorking);
          assert(versions.length >= 2 && working, 'The training checks left several versions with a working one');
          await openSection('models', 'models-list');
          assert(document.body.textContent.includes('Модели · Все версии моделей проекта: паспорта, веса и рабочая версия.')
            && text('[data-testid="models-summary"]').startsWith('Всего ' + versions.length + ' · рабочая — версия ' + working.number + '. Каждый успешный запуск'),
            '«Модели» opens lazily with the summary of versions and the working one');
          const cards = $$('[data-testid="model-version-card"]');
          assert(cards.map(item => item.dataset.versionId).join() === versions.map(version => version.id).join()
            && $$('[data-testid="model-version-card-working"]').length === 1 && card(working.id).dataset.working === 'true'
            && !$('[data-testid="model-version-card-set-working"]', card(working.id)), 'Version cards go newest first; only the working one has the badge');
          const meta = $('[data-testid="model-version-card-meta"]', card(working.id)).textContent;
          const weights = $('[data-testid="model-version-card-weights"]', card(working.id)).textContent;
          assert(meta.includes(' · Проверка smoke · ' + working.architecture + ' · ' + working.input.width + ' × ' + working.input.height)
            && weights.startsWith('Эпох ' + working.epochsCompleted + ' из ') && weights.includes(' · Веса: PT ') && weights.includes(', ONNX '),
            'A card shows date, dataset, architecture, input, epochs and weights: ' + meta + ' | ' + weights);
          for (const key of ['mAP50', 'mAP50_95', 'precision', 'recall']) {
            const best = metricBest(versions, key);
            const expected = best === null ? 0 : versions.filter(version => version.metrics[key] === best).length;
            const marked = $$('[data-metric="' + key + '"][data-best]');
            assert(marked.length === expected && marked.every(item => item.textContent.endsWith('лучшая')), 'Metric ' + key + ' marks the best version only when the values differ');
          }

          $('[data-testid="model-version-card-export"]', card(working.id)).click();
          const exported = await wait(() => $('[data-testid="model-export-result"]'), 'Export result', () => $('[data-testid="model-export"]')?.textContent);
          assert(exported.textContent.startsWith('Версия ' + working.number + ' выгружена: model-ui.zip · ') && /\d (Б|КБ|МБ)$/.test(exported.textContent),
            '«Выгрузить ZIP» shows the export plate with the file and its size: ' + exported.textContent);

          $('[data-testid="model-version-card-delete"]', card(working.id)).click();
          const dialog = await wait(() => $('[data-testid="model-version-deletion-dialog"]'), 'Deletion dialog');
          await wait(() => dialog.textContent.includes('Это рабочая версия: сначала сделайте рабочей другую.'), 'Working blocker', () => dialog.textContent);
          assert($('[data-testid="model-version-deletion-confirm"]').disabled && dialog.textContent.includes('Сейчас версию удалить нельзя:'),
            'Deleting the working version is refused in the review with the reason');
          $$('button', dialog).find(item => item.textContent === 'Отмена').click();
          await wait(() => !$('[data-testid="model-version-deletion-dialog"]'), 'Dialog closed');

          const other = versions.find(version => !version.isWorking);
          $('[data-testid="model-version-card-set-working"]', card(other.id)).click();
          await wait(() => card(other.id)?.dataset.working === 'true' && !card(working.id).dataset.working
            && text('[data-testid="models-summary"]').includes('рабочая — версия ' + other.number + '.'), 'Other version working');
          assert(true, '«Сделать рабочей» moves the badge and the summary at once');
          $('[data-testid="model-version-card-set-working"]', card(working.id)).click();
          await wait(() => card(working.id)?.dataset.working === 'true', 'Working version restored');

          $('[data-testid="model-version-card-passport"]', card(working.id)).click();
          const versionPassport = await wait(() => $('[data-testid="model-version-passport"]'), 'Passport');
          assert(versionPassport.dataset.versionId === working.id && $('[data-testid="model-version-back"]').textContent === '← К списку моделей'
            && $('[data-testid="model-version-export"]') && $('[data-testid="model-version-delete"]') && $('[data-testid="model-export-result"]'),
            '«Паспорт» opens the version passport with «Выгрузить ZIP», «Удалить», the export plate and «← К списку моделей»');
          $('[data-testid="model-version-back"]').click();
          await wait(() => $('[data-testid="models-list"]'), 'Back to the list');
          assert(true, '«← К списку моделей» returns to the list');
          return {checks};
        """;

    private const string ChecksUiImageScript = """
          const versions = await modelVersions();
          const working = versions.find(version => version.isWorking);
          await openChecks();
          const metric = typeof working.metrics.mAP50 === 'number' ? ' · mAP50 ' + working.metrics.mAP50.toFixed(3) : '';
          await wait(() => text('[data-testid="checks-version"]') === 'Версия ' + working.number + ' · рабочая' + metric, 'Working version chosen',
            () => text('[data-testid="checks-version"]'));
          assert(document.body.textContent.includes('Проверки · Прогон моделей по изображению, датасету, видео и экрану.')
            && text('[data-testid="checks-caption"]').startsWith('Веса best.pt версии ' + working.number + ' · вход ' + working.input.width + ' × '
              + working.input.height + ' px · классов ' + working.classes.length + '.') && $('[data-testid="checks-models-link"]'),
            '«Проверки» choose the working version and describe its weights, input and classes');
          assert($$('[role="tablist"][aria-label="Режим проверки"] [role="tab"]').map(item => item.textContent).join() === 'Изображение,Датасет,Видео,Экран'
            && modeTab('image').getAttribute('aria-selected') === 'true' && !panel('image').hidden, 'Modes are tabs with «Изображение» first');
          key(modeTab('image'), 'End');
          await wait(() => modeTab('screen').getAttribute('aria-selected') === 'true' && document.activeElement === modeTab('screen'), 'End');
          key(modeTab('screen'), 'Home');
          await wait(() => modeTab('image').getAttribute('aria-selected') === 'true' && panel('screen').hidden && !panel('image').hidden, 'Home');
          assert(true, 'Mode tabs follow the tablist pattern');

          setValue($('[data-testid="check-image-max-detections"]'), '2');
          const previousImage = $('[data-testid="check-image-result"]')?.dataset.checkId;
          (await wait(() => enabled($('[data-testid="check-image-pick"]')), 'Pick enabled')).click();
          await wait(() => $('[data-testid="check-image-result"]')?.dataset.checkId !== previousImage && state('check-image') === 'succeeded', 'Image check done', () => state('check-image') + ' ' + text('[data-testid="check-image-error-notice"]'));
          const preview = await wait(() => loaded($('[data-testid="check-image-preview-image"]')), 'Preview loaded');
          assert($('[data-testid="check-image-preview"]').dataset.boxes === '2' && text('[data-testid="check-image-count"]') === 'Обнаружения (2)'
            && $$('[data-testid="check-image-detections"] li').length === 2 && $$('[data-testid="check-image-detections"] li')[0].textContent.endsWith('90%')
            && $$('[data-testid="check-image-preview"] [data-detection]')[0].textContent.endsWith(' 90%'),
            'An image check draws its two boxes over the preview from the resource channel and lists them');
          assert($('[data-testid="check-image-result"]').textContent.includes('кадр.jpg') && text('[data-testid="check-image-state"]') === 'Готово',
            'The result names the file and «Готово»');
          return {checks};
        """;

    private const string ChecksUiBusyScript = """
          await openChecks();
          const previousImage = $('[data-testid="check-image-result"]')?.dataset.checkId;
          (await wait(() => enabled($('[data-testid="check-image-pick"]')), 'Pick enabled')).click();
          await wait(() => $('[data-testid="check-image-result"]')?.dataset.checkId !== previousImage && state('check-image') === 'running', 'Hanging check running');
          modeTab('screen').click();
          await wait(() => $('[data-testid="check-screen"]') && text('[data-testid="check-screen-monitor"]').startsWith('Монитор '), 'Monitors listed');
          const monitors = (await request('checks.listMonitors', {})).data;
          const primary = monitors.find(item => item.isPrimary) ?? monitors[0];
          assert(text('[data-testid="check-screen-monitor"]') === 'Монитор ' + primary.index + ' — ' + primary.width + ' × ' + primary.height + (primary.isPrimary ? ' (основной)' : ''),
            'Monitors are listed before any screenshot with the primary one chosen');
          $('[data-testid="check-screen-capture"]').click();
          const working = (await modelVersions()).find(version => version.isWorking);
          const crop = primary.width > working.input.width || primary.height > working.input.height;
          await wait(() => crop ? loaded($('[data-testid="check-screen"] [data-testid="crop-image"]')) : $('[data-testid="check-screen-whole"]'), 'Screenshot');
          assert(!crop || text('[data-testid="check-screen"] [data-testid="crop-position"]').startsWith('Рамка ' + working.input.width + ' × ' + working.input.height + ' px'),
            '«Снять экран» shows the screenshot with a frame of the input size');
          const shownBefore = $('[data-testid="check-screen-result"]')?.dataset.checkId;
          (await wait(() => enabled($('[data-testid="check-screen-start"]')), 'Screen start enabled')).click();
          await wait(() => text('[data-testid="check-screen-error-notice"]').startsWith('Уже идёт другая проверка'), 'Busy refusal',
            () => text('[data-testid="check-screen-error-notice"]'));
          assert($('[data-testid="check-screen-result"]')?.dataset.checkId === shownBefore, 'A second check is refused at once while one runs, with the Russian text');
          modeTab('image').click();
          await wait(() => !panel('image').hidden, 'Image tab');
          assert(state('check-image') === 'running' && text('[data-testid="check-image-state"]') === 'Идёт проверка', 'Switching tabs keeps the running check');
          $('[data-testid="check-image-stop"]').click();
          await wait(() => state('check-image') === 'cancelled', 'Image stopped');
          assert(text('[data-testid="check-image-state"]') === 'Остановлено' && $('[data-testid="check-image-result"]').textContent.includes('остановлена кнопкой')
            && enabled($('[data-testid="check-image-pick"]')), '«Остановить» ends the check as «Остановлено»');
          return {checks};
        """;

    private const string ChecksUiVideoScript = """
          await openChecks();
          modeTab('video').click();
          (await wait(() => enabled($('[data-testid="check-video-pick"]')), 'Video pick')).click();
          await wait(() => text('[data-testid="check-video-source"]').startsWith('улица.mp4 · 640 × 360 · '), 'Video chosen', () => text('[data-testid="check-video-source"]'));
          const working = (await modelVersions()).find(version => version.isWorking);
          await wait(() => loaded($('[data-testid="check-video"] [data-testid="crop-image"]')), 'Sample frame');
          const first = text('[data-testid="check-video-frame-time"]');
          assert(text('[data-testid="check-video"] [data-testid="crop-position"]').startsWith('Рамка ' + working.input.width + ' × ' + working.input.height + ' px')
            && first.startsWith('Кадр 0:0'), 'A sample frame from the middle carries the crop frame of the input size: ' + first);
          let another = first;
          for (let attempt = 0; attempt < 3 && another === first; attempt++) {
            (await wait(() => enabled($('[data-testid="check-video-another"]')), 'Another frame enabled')).click();
            await sleep(50);
            await wait(() => enabled($('[data-testid="check-video-another"]')), 'Another frame loaded');
            another = text('[data-testid="check-video-frame-time"]');
          }
          assert(another !== first, '«Показать другой кадр» shows another frame: ' + another);
          setValue($('[data-testid="check-video-step"]'), '0.5');
          await wait(() => /^Будет проверено около \d+ кадров\.$/.test(text('[data-testid="check-video-hint"]')), 'Step estimate', () => text('[data-testid="check-video-hint"]'));
          assert(true, '«Шаг, с» estimates the frames: ' + text('[data-testid="check-video-hint"]'));
          setValue($('[data-testid="check-video-step"]'), '0');
          await wait(() => text('[data-testid="check-video-hint"]') === 'Шаг — положительное число секунд (не меньше 1 мс).' && $('[data-testid="check-video-start"]').disabled, 'Zero step');
          assert(true, 'A zero step is refused before the start');
          setValue($('[data-testid="check-video-step"]'), '0.5');
          setValue($('[data-testid="check-video-max-detections"]'), '2');
          const previous = $('[data-testid="check-video-result"]')?.dataset.checkId;
          (await wait(() => enabled($('[data-testid="check-video-start"]')), 'Video start')).click();
          await wait(() => $('[data-testid="check-video-result"]')?.dataset.checkId !== previous && state('check-video') === 'succeeded', 'Video done', () => state('check-video') + ' ' + text('[data-testid="check-video-error-notice"]'));
          const result = $('[data-testid="check-video-result"]');
          await wait(() => $$('[data-testid="check-video-journal"] tbody tr').length === 6, 'Journal rows', () => $$('[data-testid="check-video-journal"] tbody tr').length);
          const rows = $$('[data-testid="check-video-journal"] tbody tr');
          assert(text('[data-testid="check-video-progress"]').includes(' · проверено кадров 12') && text('[data-testid="check-video-progress"]').endsWith(' · с обнаружениями 6')
            && rows[0].dataset.ordinal === '6' && rows[5].dataset.ordinal === '1' && $$('[data-testid="check-video-classes"] tbody tr').length >= 1,
            'The video check shows progress, the class summary and «Где и что обнаружено» newest first: ' + text('[data-testid="check-video-progress"]'));
          await wait(() => loaded($('[data-testid="check-video-preview-image"]')), 'Video preview');
          assert(true, 'The video preview loads from the resource channel');
          const checkId = result.dataset.checkId;
          await openSection('models', 'models-section');
          await openSection('checks', 'checks-section');
          modeTab('video').click();
          await wait(() => $('[data-testid="check-video-result"]')?.dataset.checkId === checkId && $$('[data-testid="check-video-journal"] tbody tr').length === 6,
            'Reconnected video check', () => checkId + ' vs ' + $('[data-testid="check-video-result"]')?.dataset.checkId + ' rows ' + $$('[data-testid="check-video-journal"] tbody tr').length
              + ' tab ' + modeTab('video')?.getAttribute('aria-selected') + ' panel ' + !!panel('video') + ' state ' + state('check-video'));
          assert(state('check-video') === 'succeeded', 'Coming back to «Проверки» shows the last video check again (checks.get)');
          return {checks};
        """;

    private const string ChecksUiVideoStopScript = """
          await openChecks();
          modeTab('video').click();
          (await wait(() => enabled($('[data-testid="check-video-pick"]')), 'Video pick')).click();
          await wait(() => text('[data-testid="check-video-source"]').startsWith('fake-hang.mp4 · '), 'Hanging video chosen');
          const before = $('[data-testid="check-video-result"]')?.dataset.checkId;
          (await wait(() => enabled($('[data-testid="check-video-start"]')), 'Video start')).click();
          await wait(() => $('[data-testid="check-video-result"]')?.dataset.checkId !== before && state('check-video') === 'running', 'Hanging video running');
          $('[data-testid="check-video-stop"]').click();
          await wait(() => state('check-video') === 'cancelled', 'Video stopped');
          assert(text('[data-testid="check-video-state"]') === 'Остановлено' && !$('[data-testid="check-video-stop"]'), '«Остановить» stops a video check');
          return {checks};
        """;

    private const string ChecksUiScreenScript = """
          await openChecks();
          modeTab('screen').click();
          (await wait(() => enabled($('[data-testid="check-screen-capture"]')), 'Capture enabled')).click();
          await wait(() => loaded($('[data-testid="check-screen"] [data-testid="crop-image"]')) || $('[data-testid="check-screen-whole"]'), 'Screenshot again');
          await wait(() => enabled($('[data-testid="check-screen-start"]')), 'Screen start');
          setValue($('[data-testid="check-screen-fps"]'), '5');
          setValue($('[data-testid="check-screen-max-detections"]'), '2');
          const previousScreen = $('[data-testid="check-screen-result"]')?.dataset.checkId;
          $('[data-testid="check-screen-start"]').click();
          await wait(() => $('[data-testid="check-screen-result"]')?.dataset.checkId !== previousScreen && state('check-screen') === 'running' && $$('[data-testid="check-screen-journal"] tbody tr').length >= 2
            && /^Кадров \d+ · с обнаружениями \d+ · [\d,]+ к\/с · 0:0\d\.\d от начала$/.test(text('[data-testid="check-screen-stats"]')), 'Screen live',
            () => state('check-screen') + ' | ' + text('[data-testid="check-screen-stats"]'));
          await wait(() => loaded($('[data-testid="check-screen-preview-image"]')) && $$('[data-testid="check-screen-now"] [data-chip]').length === 2, 'Screen preview');
          assert(true, 'A screen check shows frames, actual fps, time, the preview and «Сейчас на экране»: ' + text('[data-testid="check-screen-stats"]'));
          $('[data-testid="check-screen-stop"]').click();
          await wait(() => state('check-screen') === 'cancelled', 'Screen stopped');
          assert(text('[data-testid="check-screen-state"]') === 'Остановлено', '«Остановить» stops the screen check');

          setValue($('[data-testid="check-screen-fps"]'), '50');
          (await wait(() => enabled($('[data-testid="check-screen-start"]')), 'Screen start again')).click();
          await wait(() => state('check-screen') === 'running' && $$('[data-testid="check-screen-journal"] tbody tr').length === 40
            && Number(text('[data-testid="check-screen-stats"]').split(' ')[1].replace(/\s/g, '')) >= 2100, 'Ring of frames',
            () => text('[data-testid="check-screen-stats"]') + ' | ' + $$('[data-testid="check-screen-journal"] tbody tr').length);
          const rows = $$('[data-testid="check-screen-journal"] tbody tr').map(item => Number(item.dataset.ordinal));
          assert(rows.every((value, index) => index === 0 || rows[index - 1] > value), '«Последние кадры с обнаружениями» keeps the latest 40 frames, newest first');
          const checkId = $('[data-testid="check-screen-result"]').dataset.checkId;
          await openSection('models', 'models-section');
          const stopped = await (async () => {
            for (let attempt = 0; attempt < 100; attempt++) {
              const current = await request('checks.get', {projectId, checkId});
              if (current.data.state === 'cancelled') return current.data;
              await sleep(100);
            }
            return null;
          })();
          assert(stopped && stopped.stopReason === 'user', 'Leaving «Проверки» stops the screen check');
          return {checks};
        """;

    private const string ChecksUiDatasetScript = """
          await openChecks();
          modeTab('dataset').click();
          await wait(() => $('[data-testid="model-evaluations"]'), 'Evaluations');
          const versions = await modelVersions();
          const working = versions.find(version => version.isWorking);
          const datasets = (await request('datasets.list', {projectId, includeArchived: true})).data;
          const smoke = datasets.find(item => item.name === 'Проверка smoke');
          await wait(() => $$('[data-testid="evaluation-row"]').length >= 1, 'Evaluation from the API check');
          const compared = $$('[data-testid="evaluation-compared-version"]');
          assert(compared.filter(item => item.dataset.state === 'checked').length === Math.min(3, versions.length)
            && compared.find(item => item.dataset.versionId === working.id).disabled, 'The selected version and the next two are compared by default');

          await pick($('[data-testid="evaluation-dataset"]'), 'Проверка smoke · ');
          const part = smoke.counts.test > 0 ? 'Test' : 'Validation';
          await wait(() => text('[data-testid="evaluation-split"]') === part, 'Default part');
          assert(text('[data-testid="evaluation-hint"]').startsWith(smoke.counts[part === 'Test' ? 'test' : 'val'] + ' кадров в части ' + part + '.'),
            'The form takes the Test part when it has frames, otherwise Validation, and counts its frames');
          if (smoke.counts.test === 0) {
            await pick($('[data-testid="evaluation-split"]'), 'Test');
            await wait(() => text('[data-testid="evaluation-problem"]') === 'В части Test этого датасета нет кадров.' && $('[data-testid="evaluation-start"]').disabled, 'Empty Test');
            assert(true, 'An empty part is explained and blocks the start');
            await pick($('[data-testid="evaluation-split"]'), 'Validation');
          }
          const sameClasses = item => item.classMap.length === working.classes.length
            && [...item.classMap].sort((a, b) => a.index - b.index).every((entry, index) => working.classes.find(cls => cls.index === index)?.classId === entry.classId);
          const other = datasets.find(item => !item.archivedAt && item.integrityState !== 'corrupt' && item.id !== smoke.id
            && (!sameClasses(item) || item.width !== working.input.width || item.height !== working.input.height));
          if (other) {
            await pick($('[data-testid="evaluation-dataset"]'), other.name + ' · ');
            const expected = !sameClasses(other)
              ? 'Классы датасета не совпадают с классами версии (состав или порядок): проверка на нём бессмысленна.'
              : 'Размер кадров датасета ' + other.width + ' × ' + other.height + ' не совпадает со входом версии ' + working.input.width + ' × ' + working.input.height + '.';
            await wait(() => text('[data-testid="evaluation-problem"]') === expected && $('[data-testid="evaluation-start"]').disabled, 'Incompatible dataset',
              () => text('[data-testid="evaluation-hint"]'));
            assert(true, 'An incompatible dataset is explained and blocks the start: ' + expected);
            await pick($('[data-testid="evaluation-dataset"]'), 'Проверка smoke · ');
          }
          if (text('[data-testid="evaluation-split"]') !== 'Validation') await pick($('[data-testid="evaluation-split"]'), 'Validation');
          await pick($('[data-testid="evaluation-device"]'), 'Процессор');

          const before = $$('[data-testid="evaluation-row"]').length;
          (await wait(() => enabled($('[data-testid="evaluation-start"]')), 'Evaluation start')).click();
          const row = await wait(() => $$('[data-testid="evaluation-row"]').length === before + 1 && $$('[data-testid="evaluation-row"]')[0], 'New evaluation row');
          const evaluationId = row.dataset.evaluationId;
          const done = await wait(() => $('[data-evaluation-id="' + evaluationId + '"]')?.dataset.state === 'succeeded' && $('[data-evaluation-id="' + evaluationId + '"]'),
            'Evaluation done', () => $('[data-evaluation-id="' + evaluationId + '"]')?.dataset.state);
          assert($('[data-testid="evaluation-row-state"]', done).textContent === 'Готово' && done.textContent.includes('Версия ' + working.number)
            && done.textContent.includes('Проверка smoke · Validation · ' + smoke.counts.val + ' кадров') && done.textContent.includes('0.600') && done.textContent.includes('0.800'),
            '«Все проверки» follow the evaluation to «Готово» with its metrics');
          $('[data-testid="evaluation-classes-toggle"]', done).click();
          await wait(() => $('[data-testid="evaluation-classes"]') && $('[data-testid="evaluation-classes-toggle"]', done).textContent === 'Скрыть классы', 'Classes shown');
          assert($$('[data-testid="evaluation-classes"] tbody tr').length === working.classes.length, '«По классам» shows the per-class metrics');
          $('[data-testid="evaluation-classes-toggle"]', done).click();

          await wait(() => text('[data-testid="evaluation-pair"]') === 'Проверка smoke · Validation', 'Comparison pair', () => text('[data-testid="evaluation-pair"]'));
          const matrix = $('[data-testid="evaluation-matrix"]');
          const columns = $$('thead th[data-version-id]', matrix);
          const workingColumn = columns.findIndex(item => item.dataset.versionId === working.id);
          assert(workingColumn >= 0 && $$('tr[data-metric="mAP50"] td', matrix)[workingColumn + 1].textContent === '0.600',
            'The comparison matrix shows the latest successful evaluation per version');
          const runnable = $$('[data-testid="evaluation-matrix-run"]', matrix).find(item => !item.disabled);
          if (runnable) {
            const versionId = runnable.closest('th').dataset.versionId;
            assert(runnable.closest('th').textContent.endsWith('проверить') && matrix.textContent.includes('не проверялась'), 'An unchecked version reads «не проверялась» with «проверить»');
            runnable.click();
            await wait(() => !$('th[data-version-id="' + versionId + '"] [data-testid="evaluation-matrix-run"]'), 'Matrix filled',
              () => $$('[data-testid="evaluation-row"]').map(item => item.dataset.state).join());
            assert(!$$('tr[data-metric="mAP50"] td[data-best]').length, '«проверить» evaluates the version; equal values are not marked best');
          } else {
            assert($$('[data-testid="evaluation-matrix-run"]', matrix).every(item => item.disabled), 'Incompatible versions cannot be checked from the matrix');
          }

          const current = await request('datasets.get', {projectId, versionId: smoke.id});
          const renamed = await request('datasets.update', {projectId, versionId: smoke.id, name: 'fake:hang', description: '', revision: current.data.revision});
          assert(renamed.ok, 'The dataset is renamed for the hanging scenario');
          await wait(() => text('[data-testid="evaluation-dataset"]').startsWith('fake:hang · '), 'Renamed dataset in the form');
          const count = $$('[data-testid="evaluation-row"]').length;
          (await wait(() => enabled($('[data-testid="evaluation-start"]')), 'Start again')).click();
          const hanging = await wait(() => $$('[data-testid="evaluation-row"]').length === count + 1 && $$('[data-testid="evaluation-row"]')[0].dataset.state === 'running'
            && $$('[data-testid="evaluation-row"]')[0], 'Hanging evaluation');
          assert($('[data-testid="evaluation-row-state"]', hanging).textContent === 'Идёт проверка', 'A running evaluation reads «Идёт проверка»');
          $('[data-testid="evaluation-cancel"]', hanging).click();
          const cancelledId = hanging.dataset.evaluationId;
          await wait(() => $('[data-evaluation-id="' + cancelledId + '"]')?.dataset.state === 'cancelled', 'Evaluation cancelled');
          assert($('[data-testid="evaluation-row-state"]', $('[data-evaluation-id="' + cancelledId + '"]')).textContent === 'Остановлено', '«Отменить» stops the evaluation');
          const again = await request('datasets.get', {projectId, versionId: smoke.id});
          await request('datasets.update', {projectId, versionId: smoke.id, name: 'Проверка smoke', description: '', revision: again.data.revision});
          return {checks};
        """;

    private const string ChecksUiDeleteScript = """
          const versions = await modelVersions();
          const target = [...versions].reverse().find(version => !version.isWorking);
          await openSection('models', 'models-list');
          $('[data-testid="model-version-card-delete"]', card(target.id)).click();
          const dialog = await wait(() => $('[data-testid="model-version-deletion-review"]') && $('[data-testid="model-version-deletion-dialog"]'), 'Deletion review');
          assert(dialog.textContent.includes('Удалить версию ' + target.number + '?') && dialog.textContent.includes('Файлы версии: '), 'The review lists the files of the version');
          (await wait(() => enabled($('[data-testid="model-version-deletion-confirm"]')), 'Confirm enabled')).click();
          await wait(() => !card(target.id) && text('[data-testid="models-notice"]').startsWith('Версия ' + target.number + ' удалена.'), 'Version deleted');
          assert(text('[data-testid="models-summary"]').startsWith('Всего ' + (versions.length - 1) + ' · '), 'A deleted version leaves the list');
          await openSection('training', 'training-section');
          tab('history').click();
          const deleted = await wait(() => $('[data-testid="training-run-card"][data-run-id="' + target.runId + '"] [data-testid="training-run-card-version-deleted"]'),
            'Deleted version in the history');
          assert(deleted.textContent === 'Версия удалена' && !$('[data-testid="training-run-card"][data-run-id="' + target.runId + '"] [data-testid="training-run-card-version"]'),
            'The training history shows «Версия удалена» instead of the passport link');
          return {checks};
        """;
}
