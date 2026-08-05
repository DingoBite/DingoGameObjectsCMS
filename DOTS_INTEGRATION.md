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
                 checkpoint barrier -> generated state pages
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
- Generated DOTS state pages are checkpoint representations of DOTS state.
  They are not live managed mirrors and are not GROs.

Never keep the same mutable value authoritative in both DOTS and RuntimeStore.
If ownership changes, define an explicit handoff boundary.

## Factory GRO profile

`GameRuntimeEntityFactoryComponent` opts a GRO into factory projection. The GRO
is a persistent authored factory, while its products are ECS-only entities.

- A factory may create zero, one, or any number of products from
  `SetupForEntity(...)`.
- The factory component is not added to the root Entity as managed
  `IComponentData`.
- The root receives `RuntimeEntityFactoryTag` and
  `RuntimeEntityFactoryProductIdentity { ProductId = 0 }`.
- Products created with
  `RuntimeEntityFactoryEcbExtensions.CreateOwnedEntity(...)` or
  `InstantiateOwnedEntity(...)` receive `RuntimeEntityFactoryOwner` and a
  non-zero `RuntimeEntityFactoryProductIdentity`.
- Every factory product has a `ProductId`, whether or not its current signature
  contains persisted state. The id is unique inside the factory and remains
  stable for that logical product. Product id `0` is reserved for the factory
  root. `Entity`, pool slot, and slot generation are physical runtime details
  and are not persistent identity.
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

Create products with the empty-entity overload, a prepared `EntityArchetype`,
or a fully projected ECS prefab. Put the full reusable product signature on the
Entity at this boundary. Ordinary simulation should change component values or
enablement, not reconstruct what the object is. The archetype and prefab paths
require `RuntimeEntityFactoryOwner` and
`RuntimeEntityFactoryProductIdentity` in the source signature and initialize
both through `SetComponent`, avoiding post-create structural adds. The prefab
path is useful when an authored component signature was projected once and
many factory products must inherit it without a parallel type switch or builder
DSL.

Use normal `GameRuntimeComponent<TSelf>` when managed projection is useful.
Factory projection is an opt-in performance profile, not the new default for
the whole library.

## Generated DOTS state schema

Every project-owned ECS component in the hybrid contour has exactly one state
classification:

- `[RuntimeDotsPersisted]` is authoritative state that
  must survive save, join/resync, and checkpoint restore;
- `[RuntimeDotsDerived]` is recreated from Factory GROs or rebuilt by a
  deterministic post-restore system;
- `[RuntimeDotsTransient]` is a request, accumulator, or other tick-local value
  that is reset instead of restored;
- `[RuntimeDotsPresentation]` belongs only to presentation and is rebuilt after
  restore.

The Editor generator discovers these attributes, rejects missing or duplicate
classification in the project-defined component scope, reconciles active
`Type -> id` entries against the previous generated static `CreateManifest`,
and emits the component catalog, numeric layout hashes, schema hash, and
canonical codecs as C#. Removed components leave only numeric reserved ids;
no removed-component metadata is retained.
Every active component entry carries a direct `RuntimeType = typeof(T)`, and
runtime lookup is keyed by that `Type`. The generated/runtime schema contains
no string type identity, CLR/assembly names, or value signatures and never
resolves a CLR type by string.
There is no mutable DOTS type-schema JSON in `StreamingAssets`. Runtime
capture never discovers component types through reflection and never switches
on a gameplay enum or asset key. Adding a persisted component changes the one
generated schema used by disk saves, network checkpoints, and replay coverage.
An optional human-readable dump may be written to an Editor-only disposable
`.temp` directory, but it is never an input or runtime authority. External mod
manifests are a separate packaging contract and remain data files.

The schema is the one logical state model, not a mandatory physical layout.
Persistent save/network uses bounded canonical pages. Hot rollback may use
preallocated project-specific `NativeList`/`NativeArray` records and Burst jobs
for a dense archetype, but it must declare and validate complete coverage of
the same generated persisted component set. A project coverage guard must
fail when a new persisted component has neither a hot-backend record nor an
explicit deterministic immutable-factory-baseline justification. This keeps
the hot path dense without creating a second authority or a separately
maintained save model.

Factory topology and component values are separate concerns. A checkpoint row
is framed by store id, persistent factory id, and product id. Inside that row,
generated cold codecs process persisted components in fixed
`ComponentTypeId` order and encode presence, enablement where applicable, and
the component value or buffer contents. Rows are written into bounded hashed
section pages; the canonical format is not a set of per-component columns.
Factories must deterministically recreate the product signature, or provide a
project topology/materialization hook for dynamic membership before rows are
prevalidated and applied. Entity-reference buffers and renderer caches are
normally derived, not serialized.

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
3. Bring the selected immutable Factory RuntimeStores to quiescence without
   cloning, replacing, or publishing them.
4. Capture their ordinary RuntimeStore baseline section.
5. Capture generated fixed-order DOTS entity rows into bounded pages and
   validate that the journal cursor did not move.
6. Build the existing `RuntimeReplayCheckpointEnvelope` and use its
   `OverallHash` as checkpoint identity.
7. Atomically replace the retained envelope/boundary and trim the journal
   through its cursor.

Capture is read-only with respect to live Factory GROs. A capture or page/hash
failure leaves the previous checkpoint envelope, boundary, and recovery window
unchanged. There is no Snapshot GRO mirror and no
`IRuntimeDotsCheckpointExporter`/`RuntimeDotsCheckpointContext` write phase;
immutable factory stores are not republished merely to checkpoint their ECS
products.

A checkpoint group that contains RuntimeStores registers one
`RuntimeStoresReplayCheckpointParticipant` with the exact same store scope and
the generated DOTS-state participant for its ECS products. The RuntimeStore
section recreates persistent factories; the generated section then resolves
`store + factory + product` addresses, prevalidates the fixed-order rows, and
applies them. Derived, transient, and presentation state is rebuilt only after
authoritative values have been restored.

`ProvideRecoveryCheckpoint()` returns a `RuntimeRecoveryCheckpoint` containing
both the validated boundary and its exact envelope, but only while the journal
recovery window remains valid and every scoped RuntimeStore still has the
revision captured at that checkpoint. Configure it as the server-side
`RuntimeRecoveryCheckpointProvider`; a boundary-only provider cannot transport
generated checkpoint pages. RuntimeStore revision drift means the immutable
hybrid-store contract was violated, so join/resync must wait for the next
explicit checkpoint.

The replay envelope already owns completed tick and journal cursor. A project
may put domain clock, RNG, singleton state, or other useful data on its own root
GRO; the integration does not require a synthetic `StateRoot` or clock GRO.

Large snapshots are encoded as bounded hashed pages. Disk and network may
stream or compress those pages differently, but both carry the same logical
component sections. Hot replay does not pass through managed page encoding.
Generic collection patching remains atomic; the framework does not infer sparse
item-level diffs for arbitrary lists.

## Network recovery

The DingoCMS Mirror protocol extends the existing grouped baseline flow rather than
introducing another transport:

- Checkpoint and journal interest is whole-`RuntimeStore`.
- Ordinary replication and typed hot streams may retain object-level interest.
- A grouped baseline carries checkpoint hash, completed tick, and journal
  cursor together with the generated checkpoint pages.
- The server obtains boundary plus envelope from
  `RuntimeDotsCheckpointCoordinator.ProvideRecoveryCheckpoint` through
  `RuntimeRecoveryCheckpointProvider`.
- The client stages RuntimeStore baselines and projects factory roots/products.
  Its project-supplied `RuntimeCheckpointStageRestore` materializes dynamic
  topology, prevalidates and applies the generated rows against those staged
  stores, and only then may the protocol publish the group.
- The client applies journal entries after that cursor in tick/sequence order.
- A replica becomes ready only after baseline publication, journal catch-up, and
  required ECB playback.
- A sequence gap, unknown codec, checkpoint mismatch, or incomplete scope
  requests resynchronization.
- A mismatched protocol version is rejected; no compatibility adapter is
  provided.

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
              checkpoint barrier -> generated state pages
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
- Generated DOTS state pages — checkpoint-представление DOTS-состояния, а не
  постоянно синхронизируемая managed-копия и не GRO.

Нельзя одновременно считать одно изменяемое значение authoritative и в DOTS,
и в RuntimeStore. Смена владельца требует явной handoff-границы.

## Профиль Factory GRO

`GameRuntimeEntityFactoryComponent` включает factory-проекцию для GRO. GRO
является persistent authored-фабрикой, а её продукты — ECS-only Entity.

- Фабрика может создать ноль, одну или любое количество Entity из
  `SetupForEntity(...)`.
- Factory component не добавляется на root Entity как managed
  `IComponentData`.
- Root получает `RuntimeEntityFactoryTag` и
  `RuntimeEntityFactoryProductIdentity { ProductId = 0 }`.
- Продукты, созданные через
  `RuntimeEntityFactoryEcbExtensions.CreateOwnedEntity(...)` или
  `InstantiateOwnedEntity(...)`, получают `RuntimeEntityFactoryOwner` и
  ненулевой `RuntimeEntityFactoryProductIdentity`.
- Каждый продукт фабрики имеет `ProductId`, даже если в его текущей сигнатуре
  нет persisted state. Id уникален внутри фабрики и стабилен для данного
  логического продукта. Значение `0` зарезервировано за factory root. `Entity`,
  pool slot и slot generation остаются физическими runtime-деталями и не
  являются persistent identity.
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

Продукты можно создавать через перегрузку с пустой Entity, готовым
`EntityArchetype` или полностью спроецированным ECS prefab. Полная
переиспользуемая сигнатура продукта задаётся на этой границе. Обычная симуляция
изменяет значения или enablement компонентов, а не заново определяет, чем
является объект. Готовый архетип и prefab обязаны содержать
`RuntimeEntityFactoryOwner` и `RuntimeEntityFactoryProductIdentity`: helper
инициализирует оба через `SetComponent` без структурных add после создания.
Prefab-путь подходит, когда authored component signature один раз проецируется
и затем наследуется множеством продуктов фабрики без отдельного type switch или
builder DSL.

Обычный `GameRuntimeComponent<TSelf>` следует использовать там, где managed
projection полезна. Factory projection — opt-in performance profile, а не новый
обязательный режим библиотеки.

## Generated-схема DOTS-состояния

Каждый project-owned ECS-компонент hybrid-контура имеет ровно одну
классификацию состояния:

- `[RuntimeDotsPersisted]` — authoritative state,
  которое переживает save, join/resync и checkpoint restore;
- `[RuntimeDotsDerived]` — состояние, которое создаётся Factory GRO или
  детерминированно пересобирается post-restore системой;
- `[RuntimeDotsTransient]` — request, accumulator или другое tick-local
  значение, которое сбрасывается, а не восстанавливается;
- `[RuntimeDotsPresentation]` — presentation-only состояние, пересобираемое
  после restore.

Editor generator находит эти атрибуты, отклоняет отсутствующую или двойную
классификацию в заданном проектом scope, согласует активные записи `Type -> id`
с предыдущим generated static `CreateManifest` и генерирует в C# component
catalog, числовые layout hashes, schema hash и canonical codecs. Удалённые
компоненты оставляют только зарезервированные числовые ids; метаданные
удалённого компонента не сохраняются.
Каждая активная запись содержит прямой `RuntimeType = typeof(T)`, и runtime
lookup keyed by этим `Type`. Generated/runtime schema не содержит строковой
идентичности типа, CLR/assembly names или value signatures и никогда не
разрешает CLR-тип по строке.
Mutable JSON-схемы типов
DOTS в `StreamingAssets` нет. Runtime capture не использует
reflection для поиска типов и не делает switches по gameplay enum или asset
key. Добавление persisted-компонента изменяет единую generated schema,
используемую disk save, network checkpoint и replay coverage.
Опциональный человекочитаемый dump может лежать только в удаляемой Editor
`.temp` папке и не является input или runtime-authority. Внешние manifests модов
относятся к отдельному packaging-контракту и остаются data files.

Schema — единая логическая модель состояния, а не обязательный физический
layout. Persistent save/network использует bounded canonical pages. Hot rollback
может хранить плотные project-specific записи в заранее выделенных
`NativeList`/`NativeArray` и захватывать их Burst jobs, но обязан объявить и
проверить полное покрытие того же generated persisted component set. Project
coverage guard обязан падать, если новый persisted-компонент не попал в запись
hot backend и для него нет явного обоснования deterministic immutable factory
baseline. Так hot path остаётся плотным, не создавая второй authority или
отдельно поддерживаемую save-модель.

Factory topology и значения компонентов — разные задачи. Строка checkpoint
обрамляется store id, persistent factory id и product id. Внутри строки
generated cold codecs обрабатывают persisted-компоненты в фиксированном порядке
`ComponentTypeId` и кодируют presence, enablement при его наличии, а затем
значение компонента или содержимое buffer. Строки записываются в bounded hashed
pages секции; canonical format не является набором per-component columns.
Фабрика должна детерминированно пересоздать product signature либо предоставить
project topology/materialization hook для динамического membership до
prevalidation и применения строк. Буферы Entity-ссылок и renderer caches обычно
derived и не сериализуются.

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
3. Доводит выбранные immutable Factory RuntimeStore до quiescence без clone,
   replace или publish.
4. Захватывает их обычную RuntimeStore baseline section.
5. Захватывает generated fixed-order DOTS entity rows в bounded pages и
   проверяет, что journal cursor не сдвинулся.
6. Собирает существующий `RuntimeReplayCheckpointEnvelope` и использует его
   `OverallHash` как checkpoint identity.
7. Атомарно заменяет retained envelope/boundary и обрезает journal по cursor.

Capture остаётся read-only относительно живых Factory GRO. Ошибка capture,
страницы или hash оставляет предыдущий checkpoint envelope, boundary и recovery
window без изменений. Нет Snapshot GRO mirror и фазы записи через
`IRuntimeDotsCheckpointExporter`/`RuntimeDotsCheckpointContext`; immutable
factory stores не перепубликуются только ради checkpoint своих ECS-продуктов.

Checkpoint-группа с RuntimeStore регистрирует один
`RuntimeStoresReplayCheckpointParticipant` с тем же store scope и generated
DOTS-state participant для ECS-продуктов. RuntimeStore section пересоздаёт
persistent factories; generated section затем разрешает адреса
`store + factory + product`, prevalidate-ит fixed-order rows и применяет их.
Derived, transient и presentation state пересобирается только после
authoritative values.

`ProvideRecoveryCheckpoint()` возвращает `RuntimeRecoveryCheckpoint`, который
содержит и проверенный boundary, и его точный envelope, только пока journal
recovery window доступно и revision каждого scoped RuntimeStore совпадает со
значением на checkpoint. Его следует передавать как server-side
`RuntimeRecoveryCheckpointProvider`; boundary-only provider не переносит
generated checkpoint pages.
Revision drift означает нарушение immutable hybrid-store контракта, поэтому
join/resync должен ждать следующий явный checkpoint.

Replay envelope уже хранит completed tick и journal cursor. Проект может
положить domain clock, RNG, singleton state и другие полезные данные в
собственный root GRO; интеграция не требует искусственного `StateRoot` или
отдельного clock GRO.

Большие snapshots кодируются bounded hashed pages. Disk и network могут по-разному
стримить или сжимать эти страницы, но переносят одинаковые логические component
sections. Hot replay не проходит через managed page encoding. Generic collection
patching остаётся атомарным; framework не выводит sparse item-level diff для
произвольных списков.

## Сетевое восстановление

Mirror protocol DingoCMS расширяет существующий grouped baseline flow, не добавляя
второй transport:

- Interest для checkpoint и journal задаётся целыми `RuntimeStore`.
- Обычная replication и typed hot streams могут сохранить object-level interest.
- Grouped baseline содержит checkpoint hash, completed tick и journal cursor
  вместе с generated checkpoint pages.
- Сервер получает boundary и envelope через
  `RuntimeDotsCheckpointCoordinator.ProvideRecoveryCheckpoint`, переданный как
  `RuntimeRecoveryCheckpointProvider`.
- Клиент staging-ит RuntimeStore baselines и проецирует factory roots/products.
  Project-supplied `RuntimeCheckpointStageRestore` материализует динамическую
  topology, prevalidate-ит и применяет generated rows к staged stores; только
  после этого protocol может опубликовать группу.
- После cursor клиент применяет journal entries в порядке tick/sequence.
- Replica становится ready только после публикации baseline, journal catch-up и
  необходимого ECB playback.
- Sequence gap, неизвестный codec, checkpoint mismatch или неполный scope
  запрашивают resync.
- Несовпадающая версия протокола отклоняется; compatibility adapter не
  предусмотрен.

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
