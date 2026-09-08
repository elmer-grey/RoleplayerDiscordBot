using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using RPBot.VtM;

namespace RPBot;

/// <summary>
/// Обработчики slash-команд модуля VtM (Vampire: the Masquerade V20).
/// </summary>
/// <remarks>
/// <para>Сейчас обрабатываются команды привязки плееров:</para>
/// <list type="bullet">
/// <item><c>/vampire bind &lt;name&gt; [user]</c> — назначить плеера персонажу (ST only).</item>
/// <item><c>/vampire unbind &lt;name&gt;</c> — снять привязку (ST only).</item>
/// </list>
/// <para>ST-only права задаются на уровне регистрации команды
/// (<see cref="SlashCommandProperties.DefaultMemberPermissions"/>).</para>
/// <para>Сама проверка прав дублируется здесь — Discord-права могут не сработать
/// на этапе пре-регистрации у конкретных конфигураций гильдий.</para>
/// </remarks>
public sealed class VampireCommands
{
    private const string StPermission = "MANAGE_ROLES";

    // Фасады: каждый отвечает за свой домен и инкапсулирует логику.
    // Тонкий класс-фасад пересылает вызовы и держит стабильный публичный API
    // (методы и сигнатуры) для Program.cs и unit-тестов.
    private readonly VampireCharacterCommands _characters = new();
    private readonly VampireDiablerieCommands _diablerie = new();
    private readonly VampireExperienceCommands _experience = new();
    private readonly VampireMoralityCommands _morality = new();
    private readonly VampireSheetButtonHandler _sheet = new();

    /// <summary>
    /// <c>/vampire bind &lt;name&gt; [user]</c>. Только рассказчик.
    /// </summary>
    public Task HandleBindAsync(SocketSlashCommand command) => _characters.HandleBindAsync(command);

    /// <summary>
    /// <c>/vampire unbind &lt;name&gt;</c>. Только рассказчик.
    /// </summary>
    public Task HandleUnbindAsync(SocketSlashCommand command) => _characters.HandleUnbindAsync(command);

    // ── Хелперы ─────────────────────────────────────────────────────────

    /// <summary>
    /// <c>/vampire show &lt;name|user&gt; [where]</c>. Доступно всем, кто видит команду.
    /// DM (default): лист с кнопками уходит получателю в личку; в канале — ephemeral «отправлено».
    /// Public: лист публикуется embed'ом в текущем канале (без кнопок).
    /// </summary>
    public Task HandleShowAsync(SocketSlashCommand command) => _characters.HandleShowAsync(command);

    // ── Создание персонажа (визард, шаг 1) ─────────────────────────────

    // ── Создание персонажа (визард, шаг 1) ─────────────────────────────

    /// <summary>
    /// <c>/vampire create</c>. Доступно всем — игрок создаёт своего персонажа
    /// (или ST — NPC).
    /// </summary>
    /// <remarks>
    /// <para>В Этапе 1 реализован только шаг 1 «Концепция» (concept, clan, nature, demeanor, bio).
    /// Дальнейшие шаги (характеристики / способности / преимущества / штрихи)
    /// появятся в следующих релизах.</para>
    /// <para>Поведение:</para>
    /// <list type="bullet">
    /// <item>ephemeral-ответ в канал: «Запрос получен, начат процесс создания, перейдите в ЛС»;</item>
    /// <item>одновременно в ЛС игрока отправляется первый экран визарда (шаг 1).</item>
    /// </list>
    /// </remarks>
    public Task HandleCreateAsync(SocketSlashCommand command) => _characters.HandleCreateAsync(command);

    /// <summary>
    /// /vampire diablerie @attacker @victim gen:N hum:M [success:true|false]
    /// Справочный расчёт итогов диаблери по правилам V20.
    /// Не меняет листы — это решение рассказчика, который применяет результат вручную.
    /// </summary>

    /// <summary>
    /// <c>/vampire_send &lt;name&gt; [user]</c>. Только рассказчик.
    /// Принудительно отправляет лист с кнопками конкретному игроку в ЛС.
    /// </summary>
    public Task HandleSendAsync(SocketSlashCommand command) => _characters.HandleSendAsync(command);

    /// <summary>
    /// <c>/vampire_show &lt;name&gt;</c>. Публичный embed без кнопок в канале.
    /// </summary>
    public Task HandleShowPublicAsync(SocketSlashCommand command) => _characters.HandleShowPublicAsync(command);

    public Task HandleDiablerieAsync(SocketSlashCommand command) => _diablerie.HandleDiablerieAsync(command);

    // ── Кнопки под листом (vtm_btn:*) ─────────────────────────────────

    /// <summary>
    /// Обработчик нажатий на кнопки <see cref="VampireSheetComponents"/>.
    /// </summary>
    public Task HandleSheetButtonAsync(SocketMessageComponent component) => _sheet.HandleSheetButtonAsync(component);

    // ── Кнопки визарда (vtm_wiz:*) ────────────────────────────────────

    /// <summary>
    /// Обработчик нажатий на кнопки <see cref="VampireWizardComponents"/>.
    /// </summary>
    public async Task HandleWizardButtonAsync(SocketMessageComponent component)
    {
            // Если у кнопки есть arg-часть (Шаг 2: имя атрибута), пробуем TryParseWithArg.
            if (VampireWizardComponents.TryParseWithArg(
                    component.Data.CustomId, out var argAction, out var _, out var arg))
            {
                await HandleWizardButtonWithArgAsync(component, argAction, arg);
                return;
            }

            if (!VampireWizardComponents.TryParse(component.Data.CustomId, out var action, out var _))
            {
                await component.RespondAsync("⚠️ Не удалось разобрать кнопку визарда.", ephemeral: true);
                return;
            }

        // Визард живёт только в DM. Ищем сессию автора кнопки — в любой гильдии,
        // где у него есть активная сессия (для Этапа 1 — она одна).
        var session = FindActiveSessionForUser(component.User.Id);
        if (session == null)
        {
            await component.RespondAsync(
                "❌ Сессия создания персонажа не найдена. " +
                "Запустите `/vampire action:create` в канале заново.",
                ephemeral: true);
            return;
        }

        switch (action)
        {
            case VampireWizardAction.SetConcept:
                await AskAndStoreAsync(component, session, "concept",
                    "Введите **концепцию** персонажа (одной строкой, например: «циничный детектив, бывший коп»).");
                return;

            case VampireWizardAction.SetClan:
                await AskAndStoreAsync(component, session, "clan",
                    "Введите **клан** персонажа. Доступные: " +
                    string.Join(", ", VampireParameterCatalog.Clans) + ".");
                return;

            case VampireWizardAction.SetNature:
                await AskAndStoreAsync(component, session, "nature",
                    "Введите **натуру** персонажа (его истинное «я»).");
                return;

            case VampireWizardAction.SetDemeanor:
                await AskAndStoreAsync(component, session, "demeanor",
                    "Введите **маску** (Demeanor) — как персонаж выглядит для окружающих.");
                return;

            case VampireWizardAction.SetBio:
                await AskAndStoreAsync(component, session, "bio",
                    "Введите **описание** (Bio) — свободный текст, до 4000 символов. Можно пропустить.");
                return;

                        case VampireWizardAction.SetSire:
                            await AskAndStoreAsync(component, session, "sire",
                                "Введите имя **сира** (Sire) — кто обратил персонажа. Пустое сообщение = очистить.");
                            return;

                        case VampireWizardAction.ClearSire:
                            VampireCreateResolver.ApplyConceptField(session.Draft, "sire", "");
                            await RerenderWizardAsync(component, session);
                            return;

                        case VampireWizardAction.SetGeneration:
                            await AskAndStoreAsync(component, session, "generation",
                                "Введите **поколение** персонажа (число от 3 до 15; дефолт 13). Пустое сообщение = сброс к 13.");
                            return;

                        case VampireWizardAction.ResetGeneration:
                            VampireCreateResolver.ApplyConceptField(session.Draft, "generation", "13");
                            await RerenderWizardAsync(component, session);
                            return;

                        case VampireWizardAction.SkipBio:
                VampireCreateResolver.ApplyConceptField(session.Draft, "bio", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.ClearBio:
                VampireCreateResolver.ApplyConceptField(session.Draft, "bio", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.ClearConcept:
                // Сбросить concept — на случай если хочется переписать.
                VampireCreateResolver.ApplyConceptField(session.Draft, "concept", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.ClearClan:
                VampireCreateResolver.ApplyConceptField(session.Draft, "clan", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.ClearNature:
                VampireCreateResolver.ApplyConceptField(session.Draft, "nature", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.ClearDemeanor:
                VampireCreateResolver.ApplyConceptField(session.Draft, "demeanor", "");
                await RerenderWizardAsync(component, session);
                return;

            case VampireWizardAction.Next:
                            // Поведение «Далее» зависит от текущего шага.
                                        if (session.Step == VampireWizardStep.Abilities)
                            {
                                            if (!VampireAbilitiesResolver.IsAbilitiesComplete(session.Draft))
                                {
                                    await component.RespondAsync(
                                                    "❌ Шаг 3 ещё не завершён. Распределите все 27 пунктов по приоритету 13/9/5.",
                                        ephemeral: true);
                                    return;
                                }
                                            await CommitDraftAsync(component, session);
                                            await component.RespondAsync(
                                                "✅ Шаг 3 (способности) сохранён. Переходим к Шагу 4 (преимущества).",
                                                ephemeral: true);
                                            try
                                            {
                                                var dm = await component.User.CreateDMChannelAsync();
                                                await VampireWizardDmHandler.RenderDisciplinesStepAsync(dm, session);
                                            }
                                            catch (Exception ex)
                                            {
                                                BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 3→4: {ex.Message}");
                                            }
                                return;
                            }
                                        if (session.Step == VampireWizardStep.Attributes)
                                        {
                                            if (!VampireAttributesResolver.IsAttributesComplete(session.Draft))
                                            {
                                                await component.RespondAsync(
                                                    "❌ Шаг 2 ещё не завершён. Распределите все 15 пунктов по приоритету 7/5/3.",
                                                    ephemeral: true);
                                                return;
                                            }
                                            await component.RespondAsync(
                                                "✅ Шаг 2 (характеристики) сохранён. Переходим к Шагу 3 (способности).",
                                                ephemeral: true);
                                            await CommitDraftAsync(component, session);
                                            try
                                            {
                                                var dm = await component.User.CreateDMChannelAsync();
                                                await VampireWizardDmHandler.RenderAbilitiesStepAsync(dm, session);
                                            }
                                            catch (Exception ex)
                                            {
                                                BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 2→3: {ex.Message}");
                                            }
                                            return;
                                        }

                            // По умолчанию — Шаг 1 «Концепция».
                            if (!VampireCreateResolver.IsConceptComplete(session.Draft))
                            {
                                var d = session.Draft;
                                var missing = new List<string>();
                                if (string.IsNullOrWhiteSpace(d.Concept))  missing.Add("Амплуа");
                                if (string.IsNullOrWhiteSpace(d.Clan))     missing.Add("Клан");
                                if (string.IsNullOrWhiteSpace(d.Nature))   missing.Add("Натура");
                                if (string.IsNullOrWhiteSpace(d.Demeanor)) missing.Add("Маска");
                                await component.RespondAsync(
                                    "❌ Концепция ещё не заполнена. Не хватает: " + string.Join(", ", missing) + ".",
                                    ephemeral: true);
                                return;
                            }
                            // Переход 1 → 2: рендерим Шаг 2 в DM.
                            await component.RespondAsync("✅ Шаг 1 сохранён. Переходим к Шагу 2 (характеристики).", ephemeral: true);
                            await CommitDraftAsync(component, session);
                            try
                            {
                                var dm = await component.User.CreateDMChannelAsync();
                                await VampireWizardDmHandler.RenderAttributesStepAsync(dm, session);
                            }
                            catch (Exception ex)
                            {
                                BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 1→2: {ex.Message}");
                            }
                            return;

                        case VampireWizardAction.BackToConcept:
                            await component.RespondAsync("⬅ Возврат на Шаг 1 (Концепция).", ephemeral: true);
                            try
                            {
                                var dm = await component.User.CreateDMChannelAsync();
                                await VampireWizardDmHandler.RenderConceptStepAsync(dm, session);
                            }
                            catch (Exception ex)
                            {
                                BotLogger.Error(LogCategory.Discord, $"Ошибка при возврате 2→1: {ex.Message}");
                            }
                            return;

                                                case VampireWizardAction.BackToAttributes:
                                                    await component.RespondAsync("⬅ Возврат на Шаг 2 (характеристики).", ephemeral: true);
                                                    try
                                                    {
                                                        var dm = await component.User.CreateDMChannelAsync();
                                                        await VampireWizardDmHandler.RenderAttributesStepAsync(dm, session);
                                                    }
                                                    catch (Exception ex)
                                                    {
                                                        BotLogger.Error(LogCategory.Discord, $"Ошибка при возврате 3→2: {ex.Message}");
                                                    }
                                                    return;

                                                case VampireWizardAction.ResetAbilityProgress:
                                                    VampireAbilitiesResolver.ResetProgress(session.Draft);
                                                    await RerenderWizardAsync(component, session);
                                                    return;

                                                case VampireWizardAction.ResetAbilityAll:
                                                    VampireAbilitiesResolver.ResetAll(session.Draft);
                                                    await RerenderWizardAsync(component, session);
                                                    return;

                                                case VampireWizardAction.AbilityPriority:
                                                case VampireWizardAction.AbilityInc:
                                                case VampireWizardAction.AbilityDec:
                                                    {
                                                        // Эти actions несут аргументы в customId-arg и обрабатываются
                                                        // отдельным entry-point (HandleWizardSelectMenuWithArgAsync).
                                                        await component.RespondAsync("⚠️ Внутренняя ошибка визарда (ability без аргумента).", ephemeral: true);
                                                        return;
                                                    }

                                                // ── Шаг 4 «Преимущества»: кнопки (часть — обрабатывается через selectmenu) ──

                                                case VampireWizardAction.DisciplineInc:
                                                case VampireWizardAction.DisciplineDec:
                                                case VampireWizardAction.BackgroundInc:
                                                case VampireWizardAction.BackgroundDec:
                                                case VampireWizardAction.BackgroundRemove:
                                                case VampireWizardAction.BackgroundRename:
                                                case VampireWizardAction.VirtueInc:
                                                case VampireWizardAction.VirtueDec:
                                                    {
                                                        // Эти actions несут аргументы и обрабатываются отдельным selectmenu-entry-point.
                                                        await component.RespondAsync("⚠️ Внутренняя ошибка визарда (adv без аргумента).", ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.BackgroundAdd:
                                                    {
                                                        // Открываем текстовый ввод для имени нового факта.
                                                        session.PendingBackgroundOp = "add";
                                                        await component.RespondAsync(
                                                            "✏️ Введите имя нового факта биографии (например, `Стая`, `Ресурсы`, `Союзники 3`).",
                                                            ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.DisciplineRename:
                                                    {
                                                        // Открываем текстовый ввод для нового имени дисциплины Каитифа.
                                                        if (!VampireAdvantagesCatalog.IsCaitiff(session.Draft.Clan))
                                                        {
                                                            await component.RespondAsync(
                                                                "⚠️ Переименовывать можно только дисциплины Каитифа.",
                                                                ephemeral: true);
                                                            return;
                                                        }
                                                        var disciplineName = VampireCommandHelpers.ExtractCustomIdArg(component.Data.CustomId);
                                                        if (string.IsNullOrWhiteSpace(disciplineName))
                                                        {
                                                            await component.RespondAsync(
                                                                "⚠️ Не указано имя переименовываемой дисциплины.",
                                                                ephemeral: true);
                                                            return;
                                                        }
                                                        session.PendingDisciplineRename = disciplineName;
                                                        await component.RespondAsync(
                                                            $"✏️ Введите новое имя для «{disciplineName}» (например, `Анимализм`, `Прорицание`, `Воздействие`).",
                                                            ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.ResetAdvProgress:
                                                case VampireWizardAction.ResetAdvAll:
                                                    {
                                                        if (session.Step != VampireWizardStep.Advantages)
                                                        {
                                                            await component.RespondAsync("⚠️ Сброс доступен только на Шаге 4.", ephemeral: true);
                                                            return;
                                                        }
                                                        VampireAdvantagesResolver.ResetProgress(session.Draft);
                                                        await RerenderAdvantagesAsync(component, session);
                                                        return;
                                                    }

                                                case VampireWizardAction.BackToAbilities:
                                                    {
                                                        await component.RespondAsync("⬅ Возврат на Шаг 3 (способности).", ephemeral: true);
                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderAbilitiesStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при возврате 4→3: {ex.Message}");
                                                        }
                                                        return;
                                                    }

                                                case VampireWizardAction.NextAdvToBackgrounds:
                                                    {
                                                        if (session.Step != VampireWizardStep.Advantages
                                                            || session.AdvantagesSubStep != VampireWizardAdvantagesSubStep.Disciplines)
                                                        {
                                                            await component.RespondAsync("⚠️ Этот переход доступен только с Шага 4.1.", ephemeral: true);
                                                            return;
                                                        }
                                                        if (!VampireAdvantagesResolver.IsDisciplinesComplete(session.Draft))
                                                        {
                                                            await component.RespondAsync(
                                                                $"❌ Шаг 4.1 ещё не завершён. Осталось {VampireAdvantagesResolver.RemainingDisciplinePool(session.Draft)} очков дисциплин.",
                                                                ephemeral: true);
                                                            return;
                                                        }
                                                        await CommitDraftAsync(component, session);
                                                        await component.RespondAsync("✅ Шаг 4.1 (дисциплины) сохранён. Переходим к Шагу 4.2 (факты).", ephemeral: true);
                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderBackgroundsStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 4.1→4.2: {ex.Message}");
                                                        }
                                                        return;
                                                    }

                                                case VampireWizardAction.NextAdvToVirtues:
                                                    {
                                                        if (session.Step != VampireWizardStep.Advantages
                                                            || session.AdvantagesSubStep != VampireWizardAdvantagesSubStep.Backgrounds)
                                                        {
                                                            await component.RespondAsync("⚠️ Этот переход доступен только с Шага 4.2.", ephemeral: true);
                                                            return;
                                                        }
                                                        if (!VampireAdvantagesResolver.IsBackgroundsComplete(session.Draft))
                                                        {
                                                            await component.RespondAsync(
                                                                $"❌ Шаг 4.2 ещё не завершён. Осталось {VampireAdvantagesResolver.RemainingBackgroundPool(session.Draft)} очков фактов.",
                                                                ephemeral: true);
                                                            return;
                                                        }
                                                        await CommitDraftAsync(component, session);
                                                        await component.RespondAsync("✅ Шаг 4.2 (факты) сохранён. Переходим к Шагу 4.3 (добродетели).", ephemeral: true);
                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderVirtuesStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 4.2→4.3: {ex.Message}");
                                                        }
                                                        return;
                                                    }

                                                case VampireWizardAction.NextAdvToFinishing:
                                                    {
                                                        if (session.Step != VampireWizardStep.Advantages
                                                            || session.AdvantagesSubStep != VampireWizardAdvantagesSubStep.Virtues)
                                                        {
                                                            await component.RespondAsync("⚠️ Этот переход доступен только с Шага 4.3.", ephemeral: true);
                                                            return;
                                                        }
                                                        if (!VampireAdvantagesResolver.IsVirtuesComplete(session.Draft))
                                                        {
                                                            await component.RespondAsync(
                                                                $"❌ Шаг 4.3 ещё не завершён. Осталось {VampireAdvantagesResolver.RemainingVirtuePool(session.Draft)} очков добродетелей.",
                                                                ephemeral: true);
                                                            return;
                                                        }
                                                        await CommitDraftAsync(component, session);

                                                        try
                                                        {
                                                            await component.DeferAsync(ephemeral: true);
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderFinishingStepAsync(dm, session);
                                                            await component.FollowupAsync("✅ Шаг 4 сохранён. Открыт Шаг 5 «Последние штрихи».", ephemeral: true);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при переходе 4.3→5: {ex.Message}");
                                                            await component.FollowupAsync("❌ Не удалось открыть Шаг 5.", ephemeral: true);
                                                        }
                                                        return;
                                                    }

                                                case VampireWizardAction.FinishingReset:
                                                    {
                                                        if (session.Step != VampireWizardStep.FinishingTouches)
                                                        {
                                                            await component.RespondAsync("⚠️ Сброс Шага 5 доступен только из Шага 5.", ephemeral: true);
                                                            return;
                                                        }
                                                        VampireFinishingResolver.ResetFreebies(session.Draft);
                                                        await CommitDraftAsync(component, session);

                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderFinishingStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при сбросе Шага 5: {ex.Message}");
                                                        }
                                                        await component.RespondAsync("🧹 Свободные пункты возвращены в пул (15/15).", ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.FinishingDone:
                                                    {
                                                        if (session.Step != VampireWizardStep.FinishingTouches)
                                                        {
                                                            await component.RespondAsync("⚠️ Завершение Шага 5 доступно только из Шага 5.", ephemeral: true);
                                                            return;
                                                        }
                                                        await CommitDraftAsync(component, session);
                                                        await component.RespondAsync(
                                                            "✅ Шаг 5 сохранён. Финальный лист персонажа появится в Шаге 6 (следующее обновление).",
                                                            ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.FinishingFinalize:
                                                    {
                                                        if (session.Step != VampireWizardStep.FinishingTouches)
                                                        {
                                                            await component.RespondAsync("⚠️ Подтверждение Шага 5 доступно только из Шага 5.", ephemeral: true);
                                                            return;
                                                        }

                                                        // Подтверждаем, что игрок добровольно завершает Шаг 5 с непустым пулом.
                                                        // После этого специализации разрешены, но снять отметку можно только повторным
                                                        // переходом на эту страницу — поэтому явно делаем reset-флажка быть не должно.
                                                        VampireFinishingResolver.ConfirmStep5(session.Draft);
                                                        await CommitDraftAsync(component, session);

                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderFinishingStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при подтверждении Шага 5: {ex.Message}");
                                                        }
                                                        await component.RespondAsync(
                                                            "⚠ Шаг 5 заморожен. Свободные пункты больше нельзя тратить; специализации теперь доступны.",
                                                            ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.BackToAdvantages:
                                                    {
                                                        if (session.Step != VampireWizardStep.FinishingTouches)
                                                        {
                                                            await component.RespondAsync("⚠️ Возврат к 4.3 доступен только из Шага 5.", ephemeral: true);
                                                            return;
                                                        }
                                                        await CommitDraftAsync(component, session);

                                                        try
                                                        {
                                                            var dm = await component.User.CreateDMChannelAsync();
                                                            await VampireWizardDmHandler.RenderVirtuesStepAsync(dm, session);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            BotLogger.Error(LogCategory.Discord, $"Ошибка при возврате 5→4.3: {ex.Message}");
                                                        }
                                                        await component.RespondAsync("↩️ Возврат к Шагу 4.3 (добродетели).", ephemeral: true);
                                                        return;
                                                    }

                                                case VampireWizardAction.FinishingInc:
                                                    {
                                                        // Шаг 5 — SelectMenu; основная обработка идёт в HandleWizardSelectAsync.
                                                        await component.RespondAsync("⚠️ Шаг 5 работает через меню выбора, а не через кнопки.", ephemeral: true);
                                                        return;
                                                    }

                        case VampireWizardAction.ResetAttrProgress:
                            VampireAttributesResolver.ResetProgress(session.Draft);
                            await RerenderWizardAsync(component, session);
                            return;

                        case VampireWizardAction.ResetAttrAll:
                            VampireAttributesResolver.ResetAll(session.Draft);
                            await RerenderWizardAsync(component, session);
                            return;

                        case VampireWizardAction.AttrInc:
                        case VampireWizardAction.AttrDec:
                            {
                                // Эти actions на Шаге 2 несут имя атрибута в customId-arg.
                                // Парсинг и обработка делаются через отдельный entry-point из HandleWizardButtonWithArgAsync.
                                await component.RespondAsync("⚠️ Внутренняя ошибка визарда (attr без аргумента).", ephemeral: true);
                                return;
                            }

                        case VampireWizardAction.Cancel:
                VampireWizardRegistry.Instance.RemoveByUser(component.User.Id);
                try
                {
                    var dm = await component.User.CreateDMChannelAsync();
                    var msg = await dm.GetMessageAsync(session.DmMessageId ?? 0);
                    if (msg is IUserMessage um)
                    {
                        await um.ModifyAsync(m =>
                        {
                            m.Content = VampireWizardDmHandler.BuildCancelledMessage();
                            m.Components = null;
                        });
                    }
                }
                catch { /* swallow — главное что сессия снята */ }
                await component.RespondAsync("❌ Визард отменён.", ephemeral: true);
                return;

            default:
                await component.RespondAsync("⚠️ Неизвестное действие визарда.", ephemeral: true);
                return;
        }
    }

                    /// <summary>
                    /// Обработка кнопок визарда с дополнительным аргументом (Шаг 2 «Характеристики»).
                    /// Поддерживает attr_inc / attr_dec с именем атрибута в arg.
                    /// </summary>
                    private async Task HandleWizardButtonWithArgAsync(
                        SocketMessageComponent component,
                        VampireWizardAction action,
                        string arg)
                    {
                        var session = FindActiveSessionForUser(component.User.Id);
                        if (session == null)
                        {
                            await component.RespondAsync(
                                "❌ Сессия создания персонажа не найдена. " +
                                "Запустите `/vampire action:create` в канале заново.",
                                ephemeral: true);
                            return;
                        }

                        if (session.Step != VampireWizardStep.Attributes)
                        {
                            await component.RespondAsync(
                                "⚠️ Эта кнопка доступна только на Шаге 2 (характеристики). " +
                                $"Текущий шаг: {session.Step}.",
                                ephemeral: true);
                            return;
                        }

                        switch (action)
                        {
                            case VampireWizardAction.AttrInc:
                                {
                                    var dec = VampireAttributesResolver.Increment(session.Draft, arg);
                                    if (!dec.IsSuccess)
                                    {
                                        await component.RespondAsync("❌ " + dec.Message, ephemeral: true);
                                        return;
                                    }
                                    await RerenderWizardAsync(component, session);
                                    return;
                                }
                            case VampireWizardAction.AttrDec:
                                {
                                    var dec = VampireAttributesResolver.Decrement(session.Draft, arg);
                                    if (!dec.IsSuccess)
                                    {
                                        await component.RespondAsync("❌ " + dec.Message, ephemeral: true);
                                        return;
                                    }
                                    await RerenderWizardAsync(component, session);
                                    return;
                                }
                            default:
                                await component.RespondAsync("⚠️ Неизвестное действие с аргументом: " + action, ephemeral: true);
                                return;
                        }
                    }

    private static async Task AskAndStoreAsync(
        SocketMessageComponent component,
        VampireWizardSession session,
        string field,
        string prompt)
    {
        session.PendingField = field;
        await component.RespondAsync(prompt + "\n\n_(Ответьте текстом в этом же ЛС — я подставлю значение в draft.)_",
            ephemeral: true);
    }

    private static async Task RerenderWizardAsync(
        SocketMessageComponent component,
        VampireWizardSession session)
    {
        if (session.DmMessageId == null) return;
        try
        {
            var dm = await component.User.CreateDMChannelAsync();
            var msg = await dm.GetMessageAsync(session.DmMessageId.Value);
            if (msg is IUserMessage um)
            {
                    if (session.Step == VampireWizardStep.Attributes)
                    {
                        var text = VampireAttributesResolver.BuildAttributesStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForAttributesStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                    }
                    else if (session.Step == VampireWizardStep.Abilities)
                    {
                        var text = VampireAbilitiesResolver.BuildAbilitiesStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForAbilitiesStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                    }
                    else if (session.Step == VampireWizardStep.Advantages)
                    {
                        await RerenderAdvantagesInternal(um, session);
                    }
                    else
                    {
                        var text = VampireCreateResolver.BuildConceptStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForConceptStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                    }
                }
            }
            catch { /* не критично */ }
            await component.RespondAsync("✅ Обновлено.", ephemeral: true);
        }

        /// <summary>
        /// Перерисовать текущий под-шаг Шага 4 в DM.
        /// </summary>
        private static async Task RerenderAdvantagesAsync(
            SocketMessageComponent component,
            VampireWizardSession session)
        {
            if (session.DmMessageId == null) return;
            try
            {
                var dm = await component.User.CreateDMChannelAsync();
                var msg = await dm.GetMessageAsync(session.DmMessageId.Value);
                if (msg is IUserMessage um)
                {
                    await RerenderAdvantagesInternal(um, session);
                }
            }
            catch { /* не критично */ }
            await component.RespondAsync("✅ Обновлено.", ephemeral: true);
        }

        private static async Task RerenderAdvantagesInternal(IUserMessage um, VampireWizardSession session)
        {
            switch (session.AdvantagesSubStep)
            {
                case VampireWizardAdvantagesSubStep.Disciplines:
                    {
                        var text = VampireAdvantagesResolver.BuildDisciplinesStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForDisciplinesStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                        return;
                    }
                case VampireWizardAdvantagesSubStep.Backgrounds:
                    {
                        var text = VampireAdvantagesResolver.BuildBackgroundsStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForBackgroundsStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                        return;
                    }
                case VampireWizardAdvantagesSubStep.Virtues:
                    {
                        var text = VampireAdvantagesResolver.BuildVirtuesStatusMessage(session.Draft);
                        var components = VampireWizardComponents.BuildForVirtuesStep(session.Draft);
                        await um.ModifyAsync(m => { m.Content = text; m.Components = components; });
                        return;
                    }
            }
        }

    private async Task CommitDraftAsync(SocketMessageComponent component, VampireWizardSession session)
    {
        if (!component.GuildId.HasValue) return;
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        await storage.UpsertDraftAsync(session.Draft);

        // Если у игрока ещё нет активного — ставим первый черновик активным.
        var registry = VampireActiveRegistry.Instance;
        var existing = registry.GetActiveCharacterId(component.GuildId.Value, component.User.Id);
        if (!existing.HasValue)
        {
            registry.SetActiveCharacterId(component.GuildId.Value, component.User.Id, session.Draft.CharacterId);
        }
    }

    private static VampireWizardSession? FindActiveSessionForUser(ulong userId)
    {
        // На Этапе 1 у пользователя одна сессия; ищем её.
        return VampireWizardRegistry.Instance.GetByUser(userId);
    }

    /// <summary>
    /// Обработка нажатия на кнопки Grant/Spend опыта (Roadmap #34).
    /// Открывает модалку с полем «Количество».
    /// </summary>
    public Task HandleExperienceButtonAsync(SocketMessageComponent component) => _experience.HandleExperienceButtonAsync(component);

    /// <summary>
    /// Обработка сабмита модалки опыта: применяет дельту к персонажу и сохраняет.
    /// </summary>
    public Task HandleExperienceModalAsync(SocketModal modal) => _experience.HandleExperienceModalAsync(modal);

    /// <summary>
    /// Обработка нажатий кнопок блока «Мораль» (Roadmap #37):
    /// Add — открывает меню расстройств; Remove — убирает последнее; Close — закрывает.
    /// </summary>
    public Task HandleMoralityButtonAsync(SocketMessageComponent component) => _morality.HandleMoralityButtonAsync(component);

    /// <summary>
    /// Обработка выбора расстройства в SelectMenu блока «Мораль» (Roadmap #37).
    /// </summary>
    public Task HandleMoralitySelectAsync(SocketMessageComponent component) => _morality.HandleMoralitySelectAsync(component);

    /// <summary>
    /// Обработка кнопок блока «Воля» (vtm_will:*).
    /// Потратить 1 пункт воли (spend) или восстановить 1 (restore).
    /// V20 стр. 116: 1 пункт воли = +1 к одному повторному броску, либо
    /// автоматический успех при сопротивлении ярости/ротшреку, либо
    /// «игнорирование повреждений» (бросок куба воли на каждое отменяемое).
    /// </summary>
    /// <remarks>
    /// <para>Текущий шаг — только обновляем запас пунктов воли. Бросок куба
    /// воли для «игнорирования повреждений» будет добавлен отдельной фичей.</para>
    /// </remarks>
    public async Task HandleWillpowerButtonAsync(SocketMessageComponent component)
    {
        if (!VampireWillpowerComponents.TryParse(component.Data.CustomId, out var action, out var charId))
        {
            await component.RespondAsync("⚠️ Не удалось разобрать кнопку воли.", ephemeral: true);
            return;
        }
        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Кнопки воли работают только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        var character = storage.GetByCharacterId(charId);
        if (character == null)
        {
            await component.RespondAsync("❌ Чарник не найден.", ephemeral: true);
            return;
        }
        if (character.PlayerId != component.User.Id)
        {
            await component.RespondAsync("⚠️ Только владелец чарника может менять волю.", ephemeral: true);
            return;
        }

#pragma warning disable CS0618 // WillpowerPoints устарело для листа, но используется runtime-кнопкой.
        character.EnsureWillpowerPointsValid();
        var ceiling = character.Willpower;
        var current = character.WillpowerPoints;

        switch (action)
        {
            case WillpowerAction.SpendOne:
                if (current <= 0)
                {
                    await component.RespondAsync("ℹ️ Нечего тратить — запас воли пуст.", ephemeral: true);
                    return;
                }
                character.WillpowerPoints = current - 1;
                await storage.UpsertAsync(character);
                await component.RespondAsync(
                    $"✅ Потрачен 1 пункт воли. Остаток: **{character.WillpowerPoints}** / {ceiling}.",
                    components: VampireWillpowerComponents.Build(charId),
                    ephemeral: true);
                return;

            case WillpowerAction.RestoreOne:
                if (current >= ceiling)
                {
                    await component.RespondAsync(
                        $"ℹ️ Запас воли уже полный: {current} / {ceiling}.",
                        ephemeral: true);
                    return;
                }
                character.WillpowerPoints = current + 1;
                await storage.UpsertAsync(character);
                await component.RespondAsync(
                    $"✅ Восстановлен 1 пункт воли. Запас: **{character.WillpowerPoints}** / {ceiling}.",
                    components: VampireWillpowerComponents.Build(charId),
                    ephemeral: true);
                return;

            default:
                await component.RespondAsync("⚠️ Неизвестное действие воли.", ephemeral: true);
                return;
        }
#pragma warning restore CS0618
    }

    /// <summary>
    /// Обработка кнопок блока «Здоровье» (vtm_health:*).
    /// Нанести нелетальный / летальный / агравированный урон (+1 ячейка)
    /// или вылечить 1 ячейку справа. V20 стр. 92.
    /// </summary>
    public async Task HandleHealthButtonAsync(SocketMessageComponent component)
    {
        if (!VampireHealthComponents.TryParse(component.Data.CustomId, out var action, out var charId))
        {
            await component.RespondAsync("⚠️ Не удалось разобрать кнопку здоровья.", ephemeral: true);
            return;
        }
        if (!component.GuildId.HasValue)
        {
            await component.RespondAsync("Кнопки здоровья работают только на сервере.", ephemeral: true);
            return;
        }
        var storage = await VampireStorageCache.GetAsync(component.GuildId.Value);
        var character = storage.GetByCharacterId(charId);
        if (character == null)
        {
            await component.RespondAsync("❌ Чарник не найден.", ephemeral: true);
            return;
        }
        if (character.PlayerId != component.User.Id)
        {
            await component.RespondAsync("⚠️ Только владелец чарника может менять здоровье.", ephemeral: true);
            return;
        }

        character.Health ??= new HealthState(7);
        var h = character.Health;

        switch (action)
        {
            case HealthAction.ApplyNonLethal:
                if (h.IsDead)
                {
                    await component.RespondAsync("☠️ Персонаж мёртв.", ephemeral: true);
                    return;
                }
                h.ApplyNonLethal(1);
                break;
            case HealthAction.ApplyLethal:
                if (h.IsDead)
                {
                    await component.RespondAsync("☠️ Персонаж уже мёртв.", ephemeral: true);
                    return;
                }
                h.ApplyLethal(1);
                break;
            case HealthAction.ApplyAggravated:
                if (h.IsDead)
                {
                    await component.RespondAsync("☠️ Персонаж уже мёртв.", ephemeral: true);
                    return;
                }
                h.ApplyAggravated(1);
                break;
            case HealthAction.HealOne:
                h.Heal(1);
                break;
            default:
                await component.RespondAsync("⚠️ Неизвестное действие здоровья.", ephemeral: true);
                return;
        }

        await storage.UpsertAsync(character);
        var status = h.IsDead ? "☠️ Персонаж мёртв." : h.IsDestroyed ? "💀 Небоеспособен." : "✅ Состояние обновлено.";
        await component.RespondAsync(
            $"{status}\n{VampireHealthEmbed.Build(character).Description}",
            components: VampireHealthComponents.Build(charId),
            ephemeral: true);
    }
}

/// <summary>
/// Потокобезопасный кеш per-guild <see cref="VampireStorage"/>, чтобы
/// не создавать новый инстанс (и не грузить с диска) на каждой slash-команде.
/// </summary>
internal static class VampireStorageCache
{
    private static readonly System.Collections.Generic.Dictionary<ulong, VampireStorage> _byGuild = new();
    private static readonly object _gate = new();

    public static async Task<VampireStorage> GetAsync(ulong guildId)
    {
        if (_byGuild.TryGetValue(guildId, out var existing))
            return existing;

        VampireStorage? created = null;
        lock (_gate)
        {
            if (_byGuild.TryGetValue(guildId, out existing)) return existing;
            created = new VampireStorage(guildId);
            _byGuild[guildId] = created;
        }

        await created.LoadAsync();
        return created;
    }

    /// <summary>Сбросить кеш (для тестов и для изменений конфигурации).</summary>
    public static void Invalidate(ulong guildId)
    {
        lock (_gate) _byGuild.Remove(guildId);
    }
}
