using System.Globalization;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;

namespace BiStudent.Server.Services;

public sealed record FaceCheckResult(bool Ok, string Message, List<string> Problems, List<byte[]> Crops, string? SimilarTo, double SimilarScore);

public sealed class RecognitionService(IServiceScopeFactory scopes, IDataProtectionProvider protection, IConfiguration configuration,
    ILogger<RecognitionService>? logger = null) : IDisposable
{
    private const int MaximumEnrollImages = 6;
    private readonly object gate = new();
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private readonly SemaphoreSlim enrollGate = new(1, 1);
    private readonly Dictionary<(int TeacherId, int SlotId, DateOnly Date), Dictionary<int, int>> streaks = [];
    private FaceEngine? engine;
    private RecognitionSettingsDto? settingsCache;
    private bool initialized;
    private string? error;
    private int revision;
    private int trainedRevision = -1;
    private IDataProtector Protector => protection.CreateProtector("BiStudent.FaceSamples");

    public bool IsAvailable { get { EnsureEngine(); lock (gate) return engine is not null; } }
    public string? Error { get { EnsureEngine(); lock (gate) return error; } }

    private void EnsureEngine()
    {
        lock (gate)
        {
            if (initialized) return;
            initialized = true;
            try { engine = new FaceEngine(); }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Не удалось загрузить OpenCV или модели распознавания лиц.");
                error = ex.Message;
            }
        }
    }

    public void Invalidate()
    {
        lock (gate) { revision++; streaks.Clear(); settingsCache = null; }
    }

    private static bool ValidThreshold(double value) => double.IsFinite(value) && value is >= 0.30 and <= 0.70;
    private static bool ValidMargin(double value) => double.IsFinite(value) && value is >= 0.00 and <= 0.20;
    private static bool ValidFrames(int value) => value is >= 1 and <= 10;

    public async Task<RecognitionSettingsDto> GetSettingsAsync()
    {
        lock (gate) { if (settingsCache is not null) return settingsCache; }
        await settingsGate.WaitAsync();
        try { return await LoadSettingsAsync(); }
        finally { settingsGate.Release(); }
    }

    private async Task<RecognitionSettingsDto> LoadSettingsAsync()
    {
        int startRevision;
        lock (gate) { if (settingsCache is not null) return settingsCache; startRevision = revision; }
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var values = await db.AppSettings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value);
        var threshold = values.TryGetValue("recognition.threshold", out var thresholdText)
            && double.TryParse(thresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedThreshold)
            && ValidThreshold(parsedThreshold) ? parsedThreshold : FaceEngine.DefaultMatchThreshold;
        var margin = values.TryGetValue("recognition.margin", out var marginText)
            && double.TryParse(marginText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMargin)
            && ValidMargin(parsedMargin) ? parsedMargin : FaceEngine.DefaultMinimumMargin;
        var frames = values.TryGetValue("recognition.frames", out var framesText)
            && int.TryParse(framesText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFrames)
            && ValidFrames(parsedFrames) ? parsedFrames : configuration.GetValue("Recognition:FramesInRow", 3);
        if (!ValidFrames(frames)) frames = 3;
        var loaded = new RecognitionSettingsDto(threshold, margin, frames);
        // Если за время чтения был Invalidate (восстановление базы), прочитанное могло устареть — не кешируем.
        lock (gate) return revision == startRevision ? settingsCache ??= loaded : loaded;
    }

    public async Task<RecognitionSettingsDto> SaveSettingsAsync(RecognitionSettingsDto settings)
    {
        if (!ValidThreshold(settings.MatchThreshold))
            throw new InvalidOperationException("Порог сходства должен быть в диапазоне 0.30–0.70.");
        if (!ValidMargin(settings.MinimumMargin))
            throw new InvalidOperationException("Отрыв от второго кандидата должен быть в диапазоне 0.00–0.20.");
        if (!ValidFrames(settings.FramesInRow))
            throw new InvalidOperationException("Количество кадров подряд должно быть в диапазоне 1–10.");
        await settingsGate.WaitAsync();
        try
        {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var values = new Dictionary<string, string>
        {
            ["recognition.threshold"] = settings.MatchThreshold.ToString("R", CultureInfo.InvariantCulture),
            ["recognition.margin"] = settings.MinimumMargin.ToString("R", CultureInfo.InvariantCulture),
            ["recognition.frames"] = settings.FramesInRow.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var pair in values)
        {
            var stored = await db.AppSettings.FindAsync(pair.Key);
            if (stored is null) db.AppSettings.Add(new AppSetting { Key = pair.Key, Value = pair.Value });
            else stored.Value = pair.Value;
        }
        await db.SaveChangesAsync();
        lock (gate) { settingsCache = settings; streaks.Clear(); }
        return settings;
        }
        finally { settingsGate.Release(); }
    }

    private async Task TrainIfNeededAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
            if (await TrainOnceAsync()) return;
        throw new InvalidOperationException("Образцы лиц меняются слишком часто — повторите попытку.");
    }

    private async Task<bool> TrainOnceAsync()
    {
        int version;
        lock (gate) { if (trainedRevision == revision) return true; version = revision; }
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.FaceSamples.AsNoTracking().Select(x => new { x.StudentId, x.ProtectedImage }).ToListAsync();
        var samples = new List<(int StudentId, byte[] Image)>();
        foreach (var sample in stored)
        {
            try { samples.Add((sample.StudentId, Protector.Unprotect(sample.ProtectedImage))); }
            catch { /* Повреждённый образец не блокирует остальные. */ }
        }
        lock (gate)
        {
            if (engine is null) return true;
            if (version != revision) return false;
            engine.Train(samples);
            trainedRevision = version;
            return true;
        }
    }

    public async Task<RecognitionResultDto> RecognizeAsync(int teacherId, int slotId, DateOnly date, byte[] jpeg)
    {
        EnsureEngine();
        if (!IsAvailable) return new RecognitionResultDto([], 0, 0, Error);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slot = await db.ScheduleSlots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == slotId && x.TeacherId == teacherId)
            ?? throw new UnauthorizedAccessException("Пара недоступна преподавателю.");
        var students = await db.Students.AsNoTracking().Select(x => new { x.Id, x.FullName, x.GroupId }).ToDictionaryAsync(x => x.Id);
        var marked = await db.AttendanceMarks.AsNoTracking().Where(x => x.SlotId == slotId && x.Date == date)
            .Select(x => x.StudentId).ToListAsync();
        if (ImageHeader.Validate(jpeg) is { } invalid)
        {
            lock (gate) streaks.Remove((teacherId, slotId, date));
            return new RecognitionResultDto([], 0, 0, invalid);
        }
        var settings = await GetSettingsAsync();
        using var frame = Cv2.ImDecode(jpeg, ImreadModes.Color);
        if (frame.Empty())
        {
            lock (gate) streaks.Remove((teacherId, slotId, date));
            return new RecognitionResultDto([], 0, 0, "Не удалось прочитать кадр JPEG.");
        }
        IReadOnlyList<FaceMatch>? matches = null;
        HashSet<int> ready = [];
        try
        {
            for (var attempt = 0; attempt < 3 && matches is null; attempt++)
            {
            await TrainIfNeededAsync();
            lock (gate)
            {
                if (trainedRevision != revision) continue;
                var current = settingsCache ?? settings;
                engine!.MatchThreshold = current.MatchThreshold;
                engine.MinimumMargin = current.MinimumMargin;
                matches = engine.Scan(frame);
                var eligible = matches.Where(x => x.StudentId is int id && students.TryGetValue(id, out var s) && s.GroupId == slot.GroupId)
                    .Select(x => x.StudentId!.Value).ToHashSet();
                ready.Clear();
                var key = (teacherId, slotId, date);
                if (!streaks.TryGetValue(key, out var counts))
                {
                    // Чистим по серверной дате, а не по дате из запроса; предел размера — на случай произвольных дат.
                    var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
                    foreach (var old in streaks.Keys.Where(x => x.Date < cutoff).ToArray()) streaks.Remove(old);
                    if (streaks.Count >= 1000) streaks.Clear();
                    streaks[key] = counts = [];
                }
                foreach (var id in counts.Keys.Except(eligible).ToArray()) counts.Remove(id);
                foreach (var id in eligible)
                {
                    counts[id] = counts.GetValueOrDefault(id) + 1;
                    if (counts[id] >= current.FramesInRow && !marked.Contains(id)) ready.Add(id);
                }
            }
            }
        }
        catch (Exception ex) { return new RecognitionResultDto([], frame.Width, frame.Height, ex.Message); }
        if (matches is null) return new RecognitionResultDto([], frame.Width, frame.Height, "Образцы лиц обновляются — повторите через секунду.");
        var changed = new HashSet<int>();
        var attendance = scope.ServiceProvider.GetRequiredService<AttendanceService>();
        foreach (var id in ready)
            if (await attendance.MarkAsync(slotId, date, id, AttendanceStatus.Present, MarkMethod.Camera)) changed.Add(id);
        var faces = matches.Select(match => {
            var id = match.StudentId;
            var known = id is int value && students.TryGetValue(value, out var student) ? student : null;
            var candidate = match.CandidateId is int candidateId && students.TryGetValue(candidateId, out var possible)
                ? possible.FullName : null;
            var other = known is not null && known.GroupId != slot.GroupId;
            return new RecognizedFaceDto(match.Bounds.X, match.Bounds.Y, match.Bounds.Width, match.Bounds.Height,
                id, known?.FullName, match.Similarity, id is int studentId && changed.Contains(studentId),
                id is int existing && marked.Contains(existing), other, candidate, match.Margin);
        }).ToList();
        return new RecognitionResultDto(faces, frame.Width, frame.Height);
    }

    public async Task<EnrollResultDto> EnrollAsync(int studentId, IReadOnlyList<byte[]> jpegs, bool allowSimilar)
    {
        if (jpegs.Count < 3) return new EnrollResultDto(false, "Нужно не меньше трёх кадров.");
        if (jpegs.Count > MaximumEnrollImages) return new EnrollResultDto(false, $"Нужно не больше {MaximumEnrollImages} кадров.");
        await enrollGate.WaitAsync();
        try { return await EnrollCoreAsync(studentId, jpegs, allowSimilar); }
        finally { enrollGate.Release(); }
    }

    public async Task<FaceCheckResult> CheckSubmissionAsync(int studentId, IReadOnlyList<byte[]> jpegs)
    {
        if (jpegs.Count < 3) return new FaceCheckResult(false, "Нужно не меньше трёх кадров.", [], [], null, 0);
        if (jpegs.Count > MaximumEnrollImages) return new FaceCheckResult(false, $"Нужно не больше {MaximumEnrollImages} кадров.", [], [], null, 0);
        await enrollGate.WaitAsync();
        try { return await CheckCoreAsync(studentId, jpegs); }
        finally { enrollGate.Release(); }
    }

    public byte[] ProtectImage(byte[] image) => Protector.Protect(image);
    public byte[] UnprotectImage(byte[] data) => Protector.Unprotect(data);

    public async Task RunExclusiveAsync(Func<Task> action)
    {
        await enrollGate.WaitAsync();
        try { await action(); }
        finally { enrollGate.Release(); }
    }

    private async Task<EnrollResultDto> EnrollCoreAsync(int studentId, IReadOnlyList<byte[]> jpegs, bool allowSimilar)
    {
        var check = await CheckCoreAsync(studentId, jpegs);
        if (!check.Ok) return new EnrollResultDto(false, check.Message, Problems: check.Problems);
        if (check.SimilarTo is not null && !allowSimilar)
            return new EnrollResultDto(false, check.Message, check.SimilarTo, check.SimilarScore);
        await SaveSamplesAsync(studentId, check.Crops);
        return new EnrollResultDto(true, check.SimilarTo is null ? "Образцы лица сохранены."
            : $"Образцы лица сохранены. Лицо похоже на {check.SimilarTo} ({check.SimilarScore:P0}); распознавание этих студентов будет менее надёжным.",
            check.SimilarTo, check.SimilarScore);
    }

    private async Task<FaceCheckResult> CheckCoreAsync(int studentId, IReadOnlyList<byte[]> jpegs)
    {
        EnsureEngine();
        if (!IsAvailable) return new FaceCheckResult(false, Error ?? "Распознавание недоступно.", [], [], null, 0);
        var samples = new List<CapturedSample>();
        var problems = new List<string>();
        for (var i = 0; i < jpegs.Count; i++)
        {
            try
            {
                if (ImageHeader.Validate(jpegs[i]) is { } invalid) throw new InvalidOperationException(invalid);
                using var frame = Cv2.ImDecode(jpegs[i], ImreadModes.Color);
                if (frame.Empty()) throw new InvalidOperationException("Не удалось прочитать JPEG.");
                lock (gate) samples.Add(engine!.CaptureChecked(frame));
            }
            catch (Exception ex) { problems.Add($"Снимок {i + 1}: {ex.Message}"); }
        }
        if (problems.Count > 0)
            return new FaceCheckResult(false, "Некоторые снимки не подходят — переснимите их", problems, [], null, 0);
        for (var i = 0; i < samples.Count; i++)
            for (var j = i + 1; j < samples.Count; j++)
            {
                double similarity;
                lock (gate) similarity = FaceEngine.Similarity(samples[i].Embedding, samples[j].Embedding);
                if (similarity > 0.95) problems.Add($"Снимки {i + 1} и {j + 1} почти одинаковые — немного поверните голову");
            }
        if (problems.Count > 0)
            return new FaceCheckResult(false, "Некоторые снимки не подходят — переснимите их", problems, [], null, 0);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Students.AnyAsync(x => x.Id == studentId))
            return new FaceCheckResult(false, "Студент не найден.", [], [], null, 0);
        try { await TrainIfNeededAsync(); }
        catch (Exception ex) { return new FaceCheckResult(false, ex.Message, [], [], null, 0); }
        var settings = await GetSettingsAsync();
        (int StudentId, double Similarity)? similar = null;
        foreach (var sample in samples)
        {
            (int StudentId, double Similarity)? candidate;
            lock (gate) candidate = engine!.MostSimilar(sample.Embedding, studentId);
            if (candidate is { } match && (similar is null || match.Similarity > similar.Value.Similarity))
                similar = match;
        }
        string? similarTo = null;
        double similarScore = 0;
        if (similar is { } closest && closest.Similarity >= settings.MatchThreshold)
        {
            similarTo = await db.Students.AsNoTracking().Where(x => x.Id == closest.StudentId)
                .Select(x => x.FullName).SingleOrDefaultAsync();
            if (similarTo is not null)
            {
                similarScore = closest.Similarity;
            }
        }
        var message = similarTo is null ? "Снимки подходят."
            : $"Лицо похоже на {similarTo} ({similarScore:P0}). Если это другой человек — подтвердите сохранение: распознавание этих студентов будет менее надёжным.";
        return new FaceCheckResult(true, message, [], samples.Select(x => x.Image).ToList(), similarTo, similarScore);
    }

    private async Task SaveSamplesAsync(int studentId, IReadOnlyList<byte[]> crops)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var old = await db.FaceSamples.Where(x => x.StudentId == studentId).ToListAsync();
        db.FaceSamples.RemoveRange(old);
        db.FaceSamples.AddRange(crops.Select(crop => new FaceSample {
            StudentId = studentId, ProtectedImage = ProtectImage(crop), CreatedAt = DateTime.Now
        }));
        await db.SaveChangesAsync();
        Invalidate();
    }

    public void Dispose()
    {
        lock (gate) engine?.Dispose();
    }
}
