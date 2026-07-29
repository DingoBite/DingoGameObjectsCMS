# DOTS + RuntimeStore Integration

This document defines the optional high-cardinality integration profile between
Unity Entities and DingoGameObjectsCMS. It does not replace the regular
`GameRuntimeComponent<TSelf>` projection, managed `IComponentData`, Unity scene
integration, or RuntimeStore-authoritative workflows. Those remain appropriate
for bounded object counts and managed gameplay.

The hybrid profile is intended for simulations in which thousands of homogeneous
entities must run in DOTS while authored identity, persistence, commands, and
network recovery remain integrated with DingoGameObjectsCMS.

```text
GA/GAC -> immutable Factory GRO -> ECS-only entities
                                      |
                              DOTS authoritative state
                                      |
                            batched inputs/outcomes
                                      |
                     checkpoint barrier -> Snapshot GROs
```

## Authority

Every piece of state has exactly one declared authority.

- `RuntimeStore` owns GRO identity, hierarchy, persistence boundaries, authored
  state, and state explicitly declared RuntimeStore-authoritative.
- DOTS owns high-frequency simulation state explicitly declared
  DOTS-authoritative.
- Commands carry intent. They do not become an alternative state authority.
- Typed hot-state streams carry disposable observations or corrections. They do
  not become a persistent state authority.
- Snapshot GROs are checkpoint representations of DOTS state. They are not live
  managed mirrors.

Never keep the same mutable value authoritative in both DOTS and RuntimeStore.
If ownership changes, define an explicit handoff boundary.

## Factory GRO profile

`GameRuntimeEntityFactoryComponent` opts a GRO into factory projection. The GRO
is a persistent authored factory, while its products are ECS-only entities.

- A factory may create zero, one, or any number of products from
  `SetupForEntity(...)`.
- The factory component is not added to the root Entity as managed
  `IComponentData`.
- The root receives `RuntimeEntityFactoryTag`.
- Products created with
  `RuntimeEntityFactoryEcbExtensions.CreateOwnedEntity(...)` receive
  `RuntimeEntityFactoryOwner`.
- The root and products form one `LinkedEntityGroup`, so destroying the root
  disposes all owned products.
- Products do not receive individual GRO identity merely because they must be
  simulated.
- A new persistent logical object is represented by a new GRO.

The factory GRO is configuration, not mutable simulation state. Configure it
before projection. After successful projection, calls that mutate its component
data or component signature violate this profile. Runtime code does not throw or
emit automatic log spam for that violation; run `RuntimeEntityFactoryValidator`
explicitly in validation and tests. Destroying the factory GRO remains legal.

The standard factory hook remains the existing ECB contract:

```csharp
public override void SetupForEntity(
    RuntimeStore store,
    EntityCommandBuffer ecb,
    GameRuntimeObject runtimeObject,
    Entity factoryRoot)
```

Create products either with the empty-entity overload or with a prepared
`EntityArchetype`. Put the full reusable product signature on the Entity at this
boundary. Ordinary simulation should change component values or enablement, not
reconstruct what the object is. The archetype overload requires
`RuntimeEntityFactoryOwner` in the prepared archetype and initializes it through
`SetComponent`, avoiding a post-create structural add.

Use normal `GameRuntimeComponent<TSelf>` when managed projection is useful.
Factory projection is an opt-in performance profile, not the new default for
the whole library.

## Systems are laws over signatures

An ECS system describes a rule that applies to every matching component
signature. It is not an object `Update` wrapped in a `SystemBase`.

Prefer:

- `SystemAPI.Query`, `IJobEntity`, or `IJobChunk`;
- Burst-compatible unmanaged data;
- stable archetypes;
- component composition for capabilities and state;
- persistent or incrementally updated indexes;
- batched ECB playback for structural boundaries.

Do not copy all entity handles and then use repeated
`HasComponent`/`GetComponentData` calls to rediscover lifecycle or type. Do not
build a central switch over an enum, asset key, or one noun tag. A unit,
projectile, item, or building is defined by the complete component signature
consumed by systems.

Enums remain valid for genuinely closed scalar values or external protocol
discriminators. They must not replace an extensible archetype. Likewise, a tag
is useful for one orthogonal fact, but a `MageTag`, `TavernTag`, or similar noun
tag must not be the only definition of an object.

## Fixed tick, commands, and outcomes

High-cardinality simulation advances on a project-owned fixed tick. The
`RuntimeCommandsBus` accepts inputs and preserves its existing API, while
`RuntimeCommandJournal` assigns one strict sequence shared by recorded inputs
and outcomes.

- Record every accepted input.
- Record nondeterministic, external, or otherwise reconstruction-significant
  results as authoritative outcomes.
- Use `AppendOutcomeBatch(...)` for outcomes that already happened in DOTS. It
  records data and never executes the outcome again on the server.
- Encode large homogeneous outcome sets as one project-defined batch per
  type/tick/scope, not as one managed command per entity.
- Decode a batch through `RuntimeReplayCommandRegistry` into one
  `GameRuntimeCommand`; its handler applies the batch in one focused operation
  or ECB playback.

`RuntimeCommandJournalScope` is either session-wide or a sorted set of
RuntimeStore ids. An input may implement `IRuntimeCommandJournalScopeProvider`
to declare its scope. A multi-store command is valid only for a subscription
group containing the complete scope. A store-set checkpoint group must match
its RuntimeStore snapshot scope exactly. A session-wide group must snapshot
every persistent RuntimeStore in that session; it expresses this with a null
`StoreScope`, which the coordinator resolves to all stores in its realm.

Typed unreliable hot-state streams remain appropriate for interpolation,
corrections, and state that need not survive recovery. They do not replace the
reliable ordered journal.

## Checkpoints

A project explicitly requests a checkpoint. DingoGameObjectsCMS does not impose
a checkpoint interval or a mandatory state-root schema.

`RuntimeDotsCheckpointCoordinator.CaptureAtCompletedTick(...)` performs one
completed-tick barrier:

1. Verify the `ExternalTickBarrier` and completed tick.
2. Complete the relevant DOTS jobs.
3. Clone the selected Snapshot RuntimeStores into a hidden replay stage.
4. Run registered `IRuntimeDotsCheckpointExporter` implementations against
   that stage and bring it to quiescence.
5. Capture the existing `RuntimeReplayCheckpointEnvelope` from the staged
   stores and validate that the journal cursor did not move.
6. Use its `OverallHash` as checkpoint identity.
7. Atomically publish the staged stores, `RuntimeCheckpointBoundary`, and trim
   the journal through
   its cursor.

`RuntimeDotsCheckpointContext` is the only normal window in which a hybrid
snapshot exporter writes DOTS state into Snapshot GROs or shards. An export or
capture failure leaves the previous checkpoint boundary and recovery window
unchanged. It also disposes the hidden stage, so the active RuntimeStore
reference, epoch, generation, revision, contents, and dirty streams are not
touched by the failed attempt.

A checkpoint group that contains RuntimeStores must register one
`RuntimeStoresReplayCheckpointParticipant` with the exact same store scope.
Only those selected RuntimeStores are transactionally staged; arbitrary custom
checkpoint participants are not automatically rolled back. Use dedicated,
reference-closed Snapshot stores for this profile. The current replay
publication contract advances their epoch and generation after every successful
checkpoint, so connected replicas require the corresponding grouped baseline.
Do not include ordinary managed stores, immutable factory stores, or stores
holding external live `RuntimeInstance` references unless that generation
replacement is intentional.

`ProvideRecoveryBoundary()` returns a boundary only while the journal recovery
window remains valid and every scoped RuntimeStore still has the revision
captured at that checkpoint. Use this validated provider for Network V3.
RuntimeStore revision drift means the immutable hybrid-store contract was
violated, so join/resync must wait for the next explicit checkpoint.

The replay envelope already owns completed tick and journal cursor. A project
may put domain clock, RNG, singleton state, or other useful data on its own root
GRO; the integration does not require a synthetic `StateRoot` or clock GRO.

Large snapshots should be split into project-defined shards/pages or captured by
a focused checkpoint participant. Generic collection patching remains atomic;
the framework does not infer sparse item-level diffs for arbitrary lists.

## Network recovery

Mirror protocol V3 extends the existing grouped baseline flow rather than
introducing another transport:

- Checkpoint and journal interest is whole-`RuntimeStore`.
- Ordinary replication and typed hot streams may retain object-level interest.
- A grouped baseline carries checkpoint hash, completed tick, and journal
  cursor and is published atomically.
- The client applies journal entries after that cursor in tick/sequence order.
- A replica becomes ready only after baseline publication, journal catch-up, and
  required ECB playback.
- A sequence gap, unknown codec, checkpoint mismatch, or incomplete scope
  requests resynchronization.
- Protocol V2 peers are rejected; no compatibility adapter is provided.

Every checkpoint group supplies explicit journal retention limits for entries,
bytes, and age. Crossing a soft limit raises `NeedsCheckpoint`. On hard overflow
the live simulation and already caught-up clients continue, but new
join/resync/save-compaction cannot use that recovery window until the project
publishes another checkpoint.

On a client, configure the exact `JournalSubscriptionScope` and an explicit
`RuntimeJournalCatchupCompletion` hook that plays back the ECBs used by project
command handlers. The transport does not guess which project-owned ECB systems
must run.

## Pooling

For categories expected to reach thousands of short-lived logical instances,
prefer a prewarmed ECS pool:

- create the stable archetype in a bounded factory/setup phase;
- model active participation with a project-defined `IEnableableComponent`;
- keep slot and generation data in project-owned unmanaged components;
- activate by setting values and enabling the participation component;
- release by disabling it and advancing generation;
- do not instantiate/destroy or add/remove components on every reuse cycle.

Pooling is a project policy because slot layout, generation rules, capacity, and
overflow behavior are domain decisions. Factory ownership still provides group
cleanup when the owning GRO is destroyed.

## Anti-patterns

Avoid these patterns in the hybrid profile:

- one GRO per high-cardinality simulated Entity;
- full ECS-to-GRO synchronization every tick;
- mutable factory GROs used as live simulation state;
- one managed command or network message per homogeneous outcome;
- runtime `Ensure` systems that repeatedly rebuild a product signature;
- central type enums, identity tags, asset-key switches, or static catalogs
  standing in for component composition;
- `ToEntityArray` followed by per-Entity component discovery;
- temporary whole-world lists/maps rebuilt every tick;
- individual managed rendering API calls for every simulated Entity;
- structural churn where a stable archetype plus enablement expresses reuse.

## Reference example

`Examples/Crowd/HybridFactory` demonstrates an immutable factory component,
owned ECS-only products, and a Burst-scheduled system operating solely on the
product signature. `Examples/Crowd/Managed` and
`Examples/Crowd/ManagedRoot` remain examples of the regular managed profile.

## Official Unity references

- [Managed components](https://docs.unity.cn/Packages/com.unity.entities%401.2/manual/components-managed.html)
- [Enableable components](https://docs.unity.cn/Packages/com.unity.entities%401.3/manual/components-enableable-use.html)
- [Entity command buffers](https://docs.unity.cn/Packages/com.unity.entities%401.0/manual/systems-entity-command-buffers.html)
- [LinkedEntityGroup](https://docs.unity.cn/Packages/com.unity.entities%401.0/api/Unity.Entities.LinkedEntityGroup.html)

---

# Интеграция DOTS + RuntimeStore

Этот документ определяет опциональный высоконагруженный профиль интеграции
Unity Entities и DingoGameObjectsCMS. Он не заменяет обычную проекцию
`GameRuntimeComponent<TSelf>`, managed `IComponentData`, интеграцию с Unity-сценой
или сценарии, где authoritative-состояние находится в RuntimeStore. Эти режимы
остаются правильным выбором для ограниченного количества объектов и managed
геймплея.

Hybrid-профиль предназначен для симуляций, где тысячи однородных Entity должны
обрабатываться в DOTS, а authored identity, persistence, команды и сетевое
восстановление остаются интегрированы с DingoGameObjectsCMS.

```text
GA/GAC -> immutable Factory GRO -> ECS-only Entity
                                      |
                           DOTS authoritative state
                                      |
                         batched inputs/outcomes
                                      |
                    checkpoint barrier -> Snapshot GRO
```

## Authority

У каждого вида данных должен быть ровно один явно выбранный authority.

- `RuntimeStore` владеет идентичностью GRO, иерархией, persistence boundaries,
  authored state и данными, объявленными RuntimeStore-authoritative.
- DOTS владеет высокочастотным simulation state, объявленным
  DOTS-authoritative.
- Команды переносят намерение, но не становятся альтернативным authority.
- Typed hot-state streams переносят одноразовые наблюдения и коррекции, но не
  persistent state.
- Snapshot GRO — checkpoint-представление DOTS-состояния, а не постоянно
  синхронизируемая managed-копия.

Нельзя одновременно считать одно изменяемое значение authoritative и в DOTS,
и в RuntimeStore. Смена владельца требует явной handoff-границы.

## Профиль Factory GRO

`GameRuntimeEntityFactoryComponent` включает factory-проекцию для GRO. GRO
является persistent authored-фабрикой, а её продукты — ECS-only Entity.

- Фабрика может создать ноль, одну или любое количество Entity из
  `SetupForEntity(...)`.
- Factory component не добавляется на root Entity как managed
  `IComponentData`.
- Root получает `RuntimeEntityFactoryTag`.
- Продукты, созданные через
  `RuntimeEntityFactoryEcbExtensions.CreateOwnedEntity(...)`, получают
  `RuntimeEntityFactoryOwner`.
- Root и продукты входят в один `LinkedEntityGroup`, поэтому уничтожение root
  освобождает все owned products.
- Продуктам не нужен отдельный GRO только ради симуляции.
- Новый persistent логический объект представляется новым GRO.

Factory GRO — это конфигурация, а не изменяемое состояние симуляции. Его нужно
полностью настроить до projection. После успешной projection изменение данных
компонентов или component signature нарушает профиль. Runtime не бросает
исключение и не засоряет лог автоматически; нарушение проверяется явным
`RuntimeEntityFactoryValidator` в validation и тестах. Удаление factory GRO
разрешено.

Сохраняется существующий ECB-контракт:

```csharp
public override void SetupForEntity(
    RuntimeStore store,
    EntityCommandBuffer ecb,
    GameRuntimeObject runtimeObject,
    Entity factoryRoot)
```

Продукты можно создавать через перегрузку с пустой Entity или готовым
`EntityArchetype`. Полная переиспользуемая сигнатура продукта задаётся на этой
границе. Обычная симуляция изменяет значения или enablement компонентов, а не
заново определяет, чем является объект. Готовый архетип обязан содержать
`RuntimeEntityFactoryOwner`: перегрузка инициализирует его через `SetComponent`
без структурного add после создания.

Обычный `GameRuntimeComponent<TSelf>` следует использовать там, где managed
projection полезна. Factory projection — opt-in performance profile, а не новый
обязательный режим библиотеки.

## Системы — законы над сигнатурами

ECS-система описывает правило, применяемое ко всем подходящим сигнатурам. Это не
объектный `Update`, завёрнутый в `SystemBase`.

Следует использовать:

- `SystemAPI.Query`, `IJobEntity` или `IJobChunk`;
- Burst-compatible unmanaged data;
- стабильные архетипы;
- component composition для способностей и состояния;
- persistent или инкрементально обновляемые индексы;
- batched ECB playback на структурных границах.

Нельзя копировать все Entity handles, а затем серией
`HasComponent`/`GetComponentData` заново определять lifecycle или тип. Нельзя
строить центральный `switch` по enum, asset key или одному noun tag. Юнит,
снаряд, предмет или здание определяются полной сигнатурой компонентов, которую
потребляют системы.

Enum допустим для действительно закрытого scalar value или внешнего
protocol discriminator. Он не должен заменять расширяемый архетип. Аналогично,
tag полезен для одного ортогонального факта, но `MageTag`, `TavernTag` и похожие
noun tags не должны в одиночку определять объект.

## Fixed tick, команды и outcomes

Высоконагруженная симуляция выполняется на project-owned fixed tick.
`RuntimeCommandsBus` принимает inputs и сохраняет существующий API, а
`RuntimeCommandJournal` назначает одну строгую sequence для записанных inputs и
outcomes.

- Каждый принятый input записывается.
- Недетерминированные, внешние и иные значимые для восстановления результаты
  записываются как authoritative outcomes.
- `AppendOutcomeBatch(...)` применяется к уже произошедшим в DOTS результатам.
  Он только записывает данные и не исполняет outcome на сервере повторно.
- Большие однородные наборы outcomes кодируются одним project-defined batch на
  type/tick/scope, а не managed-командой на каждую Entity.
- Batch декодируется через `RuntimeReplayCommandRegistry` в одну
  `GameRuntimeCommand`; handler применяет его одной focused operation или одним
  ECB playback.

`RuntimeCommandJournalScope` бывает session-wide либо содержит отсортированный
набор RuntimeStore ids. Input может реализовать
`IRuntimeCommandJournalScopeProvider`, чтобы объявить scope. Multi-store command
валидна только для subscription group, которая содержит scope целиком.
Store-set checkpoint group должна точно совпадать со своим RuntimeStore snapshot
scope. Session-wide group обязана снимать все persistent RuntimeStore сессии:
для неё `StoreScope` равен null, а coordinator раскрывает его во все stores
своего realm.

Typed unreliable hot-state streams подходят для interpolation, коррекций и
данных, которые не обязаны пережить восстановление. Они не заменяют reliable
ordered journal.

## Checkpoint

Checkpoint явно запрашивает проект. DingoGameObjectsCMS не навязывает интервал
и обязательную схему state root.

`RuntimeDotsCheckpointCoordinator.CaptureAtCompletedTick(...)` выполняет одну
completed-tick barrier:

1. Проверяет `ExternalTickBarrier` и завершённый tick.
2. Завершает нужные DOTS jobs.
3. Клонирует выбранные Snapshot RuntimeStore в скрытый replay stage.
4. Запускает зарегистрированные `IRuntimeDotsCheckpointExporter` над stage и
   доводит его до quiescence.
5. Захватывает существующий `RuntimeReplayCheckpointEnvelope` из staged stores
   и проверяет, что journal cursor не изменился.
6. Использует его `OverallHash` как checkpoint identity.
7. Атомарно публикует staged stores, `RuntimeCheckpointBoundary` и обрезает
   journal по cursor.

`RuntimeDotsCheckpointContext` — единственное обычное окно, в котором
hybrid snapshot exporter пишет DOTS-состояние в Snapshot GRO или shards. Ошибка
export/capture оставляет предыдущую checkpoint boundary и recovery window без
изменений. Скрытый stage уничтожается, поэтому активные RuntimeStore reference,
epoch, generation, revision, содержимое и dirty streams не меняются.

Checkpoint-группа с RuntimeStore обязана зарегистрировать один
`RuntimeStoresReplayCheckpointParticipant` с точно таким же store scope.
Транзакционно staging применяется только к этим RuntimeStore; произвольные
custom checkpoint participants автоматически не откатываются. Для профиля
нужны выделенные, замкнутые по ссылкам Snapshot stores. Текущий replay publish
после каждого успешного checkpoint повышает их epoch и generation, поэтому
подключённым репликам нужен соответствующий grouped baseline. Обычные managed
stores, immutable factory stores и stores с внешними живыми `RuntimeInstance`
ссылками нельзя включать в группу, если такая смена generation не задумана.

`ProvideRecoveryBoundary()` возвращает boundary только пока journal recovery
window доступно и revision каждого scoped RuntimeStore совпадает со значением на
checkpoint. Именно этот проверенный provider следует передавать Network V3.
Revision drift означает нарушение immutable hybrid-store контракта, поэтому
join/resync должен ждать следующий явный checkpoint.

Replay envelope уже хранит completed tick и journal cursor. Проект может
положить domain clock, RNG, singleton state и другие полезные данные в
собственный root GRO; интеграция не требует искусственного `StateRoot` или
отдельного clock GRO.

Большие snapshots следует делить на project-defined shards/pages или захватывать
через focused checkpoint participant. Generic collection patching остаётся
атомарным; framework не выводит sparse item-level diff для произвольных списков.

## Сетевое восстановление

Mirror protocol V3 расширяет существующий grouped baseline flow, не добавляя
второй transport:

- Interest для checkpoint и journal задаётся целыми `RuntimeStore`.
- Обычная replication и typed hot streams могут сохранить object-level interest.
- Grouped baseline содержит checkpoint hash, completed tick и journal cursor и
  публикуется атомарно.
- После cursor клиент применяет journal entries в порядке tick/sequence.
- Replica становится ready только после публикации baseline, journal catch-up и
  необходимого ECB playback.
- Sequence gap, неизвестный codec, checkpoint mismatch или неполный scope
  запрашивают resync.
- Protocol V2 peers отклоняются; compatibility adapter не предусмотрен.

Каждая checkpoint group обязана явно задать retention limits по entries, bytes
и age. Пересечение soft limit поднимает `NeedsCheckpoint`. При hard overflow
live simulation и уже догнавшие клиенты продолжают работу, но новые
join/resync/save-compaction не могут использовать это recovery window до
публикации нового checkpoint.

На клиенте нужно явно задать точный `JournalSubscriptionScope` и
`RuntimeJournalCatchupCompletion`, который проигрывает ECB, используемые
project-owned command handlers. Transport не угадывает, какие ECB-системы
проекта должны выполниться.

## Пулинг

Для категорий с тысячами короткоживущих логических экземпляров следует
использовать prewarmed ECS pool:

- стабильный архетип создаётся в ограниченной factory/setup-фазе;
- активное участие задаётся project-defined `IEnableableComponent`;
- slot и generation хранятся в project-owned unmanaged-компонентах;
- активация записывает значения и включает participation component;
- освобождение выключает его и увеличивает generation;
- instantiate/destroy или add/remove компонентов не выполняются на каждом цикле.

Pooling остаётся project policy, потому что layout слотов, generation rules,
capacity и overflow — доменные решения. Factory ownership всё равно обеспечивает
групповую очистку при удалении owning GRO.

## Антипаттерны

В hybrid-профиле нельзя использовать:

- отдельный GRO для каждой высокочастотной Entity;
- полный ECS-to-GRO sync каждый tick;
- mutable Factory GRO как live simulation state;
- отдельную managed-команду или network message на каждый однородный outcome;
- runtime `Ensure`-системы, повторно собирающие сигнатуру продукта;
- центральные type enums, identity tags, switches по asset key или статические
  каталоги вместо component composition;
- `ToEntityArray` с последующим per-Entity component discovery;
- временные whole-world lists/maps, пересобираемые каждый tick;
- отдельный managed rendering API call для каждой Entity;
- structural churn там, где reuse выражается стабильным архетипом и enablement.

## Эталонный пример

`Examples/Crowd/HybridFactory` показывает immutable factory component, owned
ECS-only products и Burst-систему, работающую только с сигнатурой продукта.
`Examples/Crowd/Managed` и `Examples/Crowd/ManagedRoot` остаются примерами
обычного managed-профиля.

## Официальные материалы Unity

- [Managed components](https://docs.unity.cn/Packages/com.unity.entities%401.2/manual/components-managed.html)
- [Enableable components](https://docs.unity.cn/Packages/com.unity.entities%401.3/manual/components-enableable-use.html)
- [Entity command buffers](https://docs.unity.cn/Packages/com.unity.entities%401.0/manual/systems-entity-command-buffers.html)
- [LinkedEntityGroup](https://docs.unity.cn/Packages/com.unity.entities%401.0/api/Unity.Entities.LinkedEntityGroup.html)
