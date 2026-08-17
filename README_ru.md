# DingoGameObjectsCMS

`DingoGameObjectsCMS` — это content-first runtime framework для Unity, в котором игровое поведение описывается versioned JSON asset-ами, во время выполнения собирается в дерево runtime-объектов, а затем при необходимости материализуется в ECS, сеть, view и persistent data. Каноническая authored-библиотека — это набор внешних module package на диске; Unity asset-ы не являются вторым изменяемым источником истины.

Идея фреймворка простая:

1. Вы описываете объект не кодом сцены, а JSON-документом `GameAsset`.
2. `GameAsset` собирает `GameRuntimeObject` с набором `GameRuntimeComponent`.
3. `GameRuntimeObject` живёт в `RuntimeStore`, который умеет хранить дерево объектов, отслеживать dirty-изменения, публиковать потоки изменений и связывать runtime-объекты с ECS entity.
4. Тот же runtime-слой может быть использован для:
   - построения ECS entity;
   - сетевой репликации;
   - команд;
   - моддинга;
   - persistent data.

Это не просто “CMS для ScriptableObject”, а унифицированная модель игры, где внешний content catalog, runtime model, ECS bridge, replication и modding используют один и тот же язык данных.

Опциональный высоконагруженный DOTS-профиль описан в [Интеграции DOTS + RuntimeStore](DOTS_INTEGRATION.md).

## Почему это решение полезно

- **Content-first архитектура.** Игра описывается asset-ами и runtime-компонентами, а не разрастающимся набором scene-specific MonoBehaviour.
- **Версионность как часть модели.** Asset имеет `GameAssetKey` и `GUID`, поэтому изменение shape данных можно оформлять новой версией asset-а, а не ломать старые сейвы и профили.
- **Одна модель для нескольких слоёв.** Тот же runtime-объект может стать ECS entity, network payload, persistent root object или mod asset.
- **Явный runtime store.** Состояние игры живёт не “размазано по сцене”, а в деревьях `RuntimeStore`, которые легко сериализовать, синхронизировать и анализировать.
- **Статическая data platform.** `RuntimeStores` одновременно держит server/client realm, а `RuntimeExecutionContext` выбирает активную фазу исполнения и active side для high-level кода.
- **Dirty-by-design.** Store копит структурные и компонентные изменения и публикует их как отдельные потоки, поэтому нет необходимости каждый раз пересылать весь мир.
- **Слабая связность сериализации и сети.** Runtime serialization вынесена в `IRuntimePayloadSerializer`, а Mirror работает поверх этой абстракции.
- **Mod-friendly storage.** Внешний base package и дополнительные mod package используют одинаковые ключи, сериализацию и единый резолвер.
- **Гибкий runtime authoring.** Система одинаково поддерживает и authored content, и runtime-created domain objects вроде профилей, настроек, meta и save state.

## Ключевые концепции

### `GameAssetKey`

`GameAssetKey` состоит из:

- `Mod`
- `Type`
- `Key`
- `Version`

Канонический layout модулей внутри `Application.persistentDataPath/assets`:

```text
<mod>/manifest.json
<mod>/<type>/<key>/<key>@<version>.json
<mod>/<resource folders...>
```

Пример:

```text
base/characters/player/player@1.2.0.json
```

Правило резолва версии:

- `version == null` означает точный запрос к `0.0.0`
- `version == ""` или пробельная строка означает запрос `latest`
- `latest` выбирается по максимальной канонической числовой версии `major.minor.patch` внутри того же `(mod, type, key)`

Это даёт удобный компромисс:

- код может жёстко запросить конкретную shape-версию;
- интеграционный слой может запросить “самый новый совместимый asset”.

### `GameAssetScriptableObject`

Внутренний Unity-тип, в который фреймворк восстанавливает документ. Содержит:

- `GameAssetKey`
- уникальный `GUID`

`GUID` идентифицирует конкретный asset/version instance. Это отдельная сущность относительно `GameAssetKey`. Экземпляры этого типа всё ещё могут использоваться в тестах или инструментах, но Unity `.asset` не является каноническим authoring-форматом.

### `GameAsset`

`GameAsset` — это versioned description объекта. Он хранит список `GameAssetComponent` и умеет:

- собирать `GameRuntimeObject` через `SetupRuntimeObject(...)`
- собирать `GameRuntimeCommand` через `CreateRuntimeCommand()`

Именно здесь asset-модель превращается в runtime-модель.

### `GameRuntimeObject`

`GameRuntimeObject` — базовый runtime-узел дерева. Он хранит:

- `Key`
- `AssetGUID`
- `SourceAssetKey`
- список `GameRuntimeComponent`
- `InstanceId`
- `StoreId`
- `Realm`

Также он умеет:

- добавлять и заменять runtime-компоненты
- отслеживать dirty-изменения по данным и по структуре компонентов
- создавать ECS entity через `CreateEntity(...)`
- держать runtime-link к `RuntimeStore`, editing-context и связанной `Entity`

Правило dirty:

- `TakeRW<T>()` автоматически помечает компонент как dirty;
- если система мутирует `GameRuntimeComponent` через уже захваченную ссылку, результат выборки или `RuntimeInstance` lookup, она обязана явно вызвать `SetDirty(...)` на `GameRuntimeObject` или `RuntimeStore`.
`SourceAssetKey` нужен для source/presentation linkage и смежных runtime-кейсов. Он не используется как lineage версии и не является наследованием — для этого есть `GameAssetPrefab`.

### `GameAssetPrefab`

`GameAsset` может объявить `Prefab` вместо дублирования другого ассета: точный ключ базы плюс четыре разреженных списка — `RemovedComponents`, `OverrideComponents`, `RemovedFields`, `OverrideFields`. Применяются в этом порядке.

Компоненты адресуются алиасом `$type`; компонент из `OverrideComponents` заменяет одноимённый из базы, а если его там нет — добавляется, всегда целиком. Всё остальное адресуется путём, укоренённым в списке компонентов:

```json
"Prefab": {
  "Base": { "Mod": "base", "Type": "static", "Key": "tavern_mage", "Version": "0.0.0" },
  "OverrideFields": { "/UnitStack_GAC/InitialMemberCount": 1 }
}
```

Сегмент `..` поднимает на уровень выше, поэтому соседний компонент — это `/A_GAC/../B_GAC/Field`, а корневое поле документа — `/../Surfaces/0/Width`. Свойства идентичности и композиции остаются недостижимыми, каким бы путём до них ни добираться.

Путь достаёт любой вложенный лист, поэтому переопределение одного поля не требует переписывать его родителя. Значение заменяется целиком — глубокого мержа нет, — что делает оверрайд коллекции атомарным ровно как в рантайм-патчах. `OverrideFields` применяется в ординальном порядке путей, так что результат не зависит от того, в каком порядке пути перечислены в документе.

Позиция компонента в документе не значит ничего: порядок материализации задаётся атрибутом `[GameAssetSetupOrder(n)]` на типе компонента, меньшее значение раньше, поэтому дописанный компонент ведёт себя так же, как авторский. Компоненты без атрибута имеют порядок `0` и сохраняют между собой авторский порядок.

Композиция выполняется в `GameAssetDocumentComposer` при загрузке документа модуля, до десериализации, поэтому вся цепочка ниже — материализация, immutable library lock, сеть — видит обычный полный ассет и не отличает производный от продублированного. База может лежать в любом смонтированном модуле. Собранный документ не сохраняет объявление prefab, так что повторная композиция — no-op; lineage читается из сырого документа через `GameAssetDocumentComposer.TryReadBaseKey`.

### `GameRuntimeCommand`

`GameRuntimeCommand` — это runtime payload для командной стороны. Он использует тот же компонентный язык, что и `GameRuntimeObject`, но представляет не persistent-узел в дереве store-а, а намерение на исполнение.

Практический смысл:

- `GameAsset` может собирать команду через `CreateRuntimeCommand()`;
- команда, как и объект, состоит из `GameRuntimeComponent`;
- команды потребляются через `RuntimeCommandsBus`, а не хранятся как runtime world state внутри `RuntimeStore`.

Это позволяет держать object state и gameplay intent в одном словаре данных, не заставляя команды вести себя как обычные runtime-объекты.

### `GameRuntimeComponent`

`GameRuntimeComponent` — базовый класс runtime-компонента. Он определяет runtime shape объекта и при необходимости участвует в ECS-проекции через:

- `SetupForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)`
- `AddForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)`
- `RemoveFromEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)`

Это важная граница:

- если компонент только хранит данные, он может вообще не участвовать в ECS;
- если компонент нужен для simulation, он добавляет нужные ECS-компоненты;
- если компонент должен уходить в dirty/sync, он реализует соответствующие dirty-маркеры.

В текущем контуре ECS-facing контракт строится вокруг `EntityCommandBuffer`, а не вокруг прямого доступа к `World`. Это нужно, чтобы структурные изменения были консистентны, когда `GameRuntimeObject` ещё работает с deferred entity из того же editing-scope.

## Архитектура данных

### `RuntimeStore`

`RuntimeStore` — это дерево runtime-объектов, которое:

- хранит все объекты store-а
- разделяет корневые объекты и дочерние
- хранит parent/child связи
- связывает `RuntimeInstance.Id` с ECS `Entity`
- копит dirty-операции
- публикует три потока изменений:
  - `StructureChanges`
  - `ComponentStructureChanges`
  - `ComponentChanges`

Поддерживаемые структурные операции:

- `Create`
- `CreateChild`
- `AttachChild`
- `DetachChild`
- `MoveChild`
- `Remove`

Удаление поддерживает несколько режимов:

- удаление поддерева
- удаление узла с переносом детей в корень
- удаление узла с перепривязкой детей к родителю

Это даёт не просто “список сущностей”, а полноценную иерархическую runtime-модель.

### `RuntimeStores`

`RuntimeStores` — это статическая data platform entry point.

Она отвечает за:

- хранение server-side `RuntimeStore`
- хранение client-side `RuntimeStore`
- net-direction metadata
- ECS `World`, который используется при создании новых store-ов


Перед созданием или резолвом store-ов нужно вызвать `RuntimeStores.SetupWorld(world)`. Если валидный `World` не зарегистрирован, создание store-а завершается fail-fast.

Это низкоуровневый слой. Он знает сразу про оба realm и нужен инфраструктуре вроде репликации, ECS-linking и snapshot apply.

### `RuntimeExecutionContext`

`RuntimeExecutionContext` — это high-level execution selector поверх `RuntimeStores`.

Он даёт:

- текущую фазу исполнения
- стабильную runtime role
- active read realm
- active write realm
- флаг, можно ли сейчас мутировать store-ы
- active dictionary store-ов для текущей фазы

Это позволяет project-коду не хардкодить `ServerStores` и `ClientStores` напрямую.

### `RS`

`RS` — узкая high-level точка доступа к store-слою для приложенческого кода.

Модель использования:

- вызывается `RS.Bind(storeId)`
- наружу отдаётся `IReadonlyBind<RuntimeStore>`
- текущий active store читается через `bind.V`

`RS` резолвит store через `RuntimeExecutionContext`, читает только уже существующий store в active realm и автоматически перепривязывает bind при смене execution side.

Если `bind.V == null`, значит store ещё не был построен или подложен другим слоем. Явное создание/загрузка store-а должно идти через инфраструктурный код, например `RuntimeStores` или `RS.Set(...)`.


Рекомендуемое правило:

- инфраструктурный код может явно работать с `RuntimeStores`;
- модели, binders и view-слой должны предпочитать `RS`.

### Dirty model

Ключевые маркеры:

- `IStoreDataDirty` — изменения данных компонента должны попадать в dirty-репликацию
- `IStoreStructDirtyIgnore` — структурные изменения такого компонента игнорируются

Практический смысл:

- вы явно контролируете, что уходит в delta;
- компоненты, важные только локально, не обязаны шуметь в сети;
- структура store-а и данные компонентов живут как разные каналы изменений.


Явное dirty-уведомление:

- `GameRuntimeObject.SetDirty<T>()`
- `GameRuntimeObject.SetDirtyById(...)`
- `RuntimeStore.SetDirty<T>(instanceId)`
- `RuntimeStore.SetDirty<T>(runtimeInstance)`

Это используется, когда данные были изменены вне обычного пути `TakeRW<T>()`. Типичный кейс: ECS- или bridge-система находит `GRC`, мутирует его поля напрямую и потом явно сообщает store-у, что данные компонента изменились.
### Realm и направление сети

Фреймворк поддерживает разделение store-ов по realm:

- `StoreRealm.Server`
- `StoreRealm.Client`

А интеграционный слой может дополнительно разделять store-ы по направлению сети:

- `None`
- `S2C`
- `C2S`

Это позволяет иметь одинаковые store id на сервере и клиенте, но разную политику владения и репликации.

## Поток данных

### Asset -> Runtime -> ECS

Основной поток выглядит так:

```text
GameAsset
  -> SetupRuntimeObject(...)
  -> GameRuntimeObject
  -> RuntimeStore
  -> CreateEntity(...)
  -> ECS Entity + ECS Components
```

Важно, что ECS здесь не является источником истины. Источник истины — runtime model.

Для ECS-проекции теперь есть два разных слоя:

- мутация `GameRuntimeObject` / `GameRuntimeComponent`, которая меняет authoritative runtime data;
- ECS projection hooks на `GameRuntimeComponent`, которые через `EntityCommandBuffer` материализуют или снимают ECS-side представление.

Во время начальной ECB-проекции корневая entity содержит `RuntimeProjectionPending`. Последняя projection-команда удаляет tag, поэтому generic ECS consumers могут явно исключать ещё не полностью собранные roots.

Это специально не universal always-live two-way sync. Runtime data может оставаться authoritative в `RuntimeStore`, а высокочастотная simulation при этом может жить в DOTS.

### Asset -> Runtime -> View

View-слой может подписываться на runtime-объекты через `GameRuntimeObjectView` и `GameRuntimeObjectsCollection`, не ломая separation между данными и отображением.

### Asset -> Runtime -> Network

Сеть синхронизирует не ECS напрямую, а `RuntimeStore`. Это даёт:

- одинаковую модель для authoritative state и replication
- предсказуемую сериализацию
- возможность делать full snapshot и delta на одном и том же слое

### Runtime -> Persistence

Фреймворк хорошо подходит и для persistent data:

- `settings`
- `profiles`
- `metas`
- `saves`

Текущий рекомендуемый паттерн:

- рассматривать persistent data как обычные именованные `RuntimeStore`; отдельного special-case слоя “только для persistence” фреймворк не вводит;
- использовать authored asset-ы только там, где данные действительно выигрывают от versioned content authoring; профили, настройки, meta и похожие domain-данные могут создаваться кодом как runtime roots;
- строить topology store-ов по предметной области и паттернам доступа, а не по искусственному делению на “persistent” и “gameplay”;
- policy миграций и disk/cloud save держать на уровне проекта.

В самом `DingoGameObjectsCMS` нет готового disk/cloud persistence service, но он уже даёт runtime model, serialization primitives и store-level change tracking, на которых такой слой можно собрать.

## Сериализация

Сериализация построена вокруг абстракции `IRuntimePayloadSerializer`.

Что это даёт:

- runtime-слой не зависит от конкретного формата
- Mirror не является владельцем сериализации
- текущий JSON можно позже заменить на бинарный или другой оптимизированный формат

Текущее состояние:

- дефолтный serializer — `JsonRuntimePayloadSerializer`
- глобальная точка подмены — `RuntimePayloadSerialization`
- для runtime-компонентов используется generated compiled таблица `Type -> Id` с прямыми CLR `Type`, компактными числовыми ids, числовыми резервациями и `RegistryHash`

Обязательный project artifact:

```text
<project>/Generated/RuntimeComponentTypes.Generated.cs
<project>/Generated/RuntimePatchCodecs.Generated.cs
```

Он компилируется в player и нужен для:

- сетевой репликации runtime-компонентов
- десериализации runtime-компонентов по `compTypeId`

Entry содержит только компактный payload `Id` и прямой `RuntimeType = typeof(T)`. Идентичностью для генератора и runtime является сам CLR `Type`; строковых component key, type name, assembly name и alias-контракта нет. Удалённые слоты сохраняются только как числовые `ReservedIds`, без метаданных удалённого CLR-типа. `RegistryHash` покрывает активную таблицу `Type -> Id` и числовые резервации и предназначен для проверки совместимости между build-ами. Runtime не читает эту таблицу из JSON и не ищет типы компонентов reflection-ом.

JSON `GameRuntimeObject` и `GameRuntimeCommand` кодирует каждый компонент явной парой: числовой `TypeId` и `Payload`. Json.NET `$type`, serialization binder, CLR-имена, имена assembly, runtime-сканирование assembly и legacy aliases не используются.

Во время активной разработки таблица регенерируется через project binding для `RuntimeComponentTypeManifestGenerator` или build preprocess. Эта привязка задаёт явный project scope активных типов и напрямую передаёт предыдущий generated static `CreateManifest`; поиска provider по имени нет. Типы, вышедшие из scope, исчезают из активной таблицы, а их ids становятся append-only числовыми резервациями. Диагностический JSON при необходимости допустим только в удаляемой Editor `.temp` папке и никогда не является runtime-authority. Это не относится к внешним `manifest.json` модов: они остаются данными mod packaging protocol.

Generated artifact с patch-кодеками следует тому же правилу. Активные записи
компонентов keyed by прямыми `Type`. Generated codecs адресуют поля компактными
числовыми ids и прямым доступом к членам. Patch-схема содержит только активные
компоненты и их текущую generated-разметку полей: удалённые patch-поля не
оставляют tombstones или имена. Постоянные резервации component slots относятся
к отдельному runtime component type registry. Runtime-binary и authoring patches
сохраняют только `ComponentTypeId`/`FieldId`; CLR-типы и `FieldInfo` существуют
только в compiled lookup-таблицах. Artifact не содержит строковой идентичности
компонентов, CLR/assembly names, value signatures или записей об удалённых
элементах. `SchemaHash`, прямые кодеки и static `CreateManifest` лежат в одном
compiled C# authority. Генератор patch-схемы не читает/не публикует mutable
`runtime_patch_schema.json`.

## Сетевая синхронизация

Mirror остаётся транспортом/фреймворком, а runtime protocol DingoCMS
синхронизирует authoritative поколения `RuntimeStore` через стек
`RuntimeProtocol*`. Строгий session manifest проверяет protocol version, build,
runtime schema и точный GA catalog до создания replica store. В модуле есть
только одна текущая реализация протокола без compatibility adapter для старых
wire-контрактов.
Parent-first binary baseline и ordered reliable delta сначала полностью
собираются в staging и только потом публикуются атомарно. Delivery sequence,
baseline id, store revision, ACK, bounded pending queue и resync — независимые
понятия протокола.

Реплицируемые данные явно делятся на три класса:

- `StructuralReliable`: наличие object и GRC, hierarchy/order,
  client-visible ownership и construction;
- `ReliableState`: долговечное semantic state — health, inventory, carried
  units, match state;
- `HotUnreliable`: transform, velocity, aim, projectile/combat samples и
  animation.

Hot state использует `RuntimeStateStreamProfile<TSample>`,
`RuntimeStateStreamCollector<TSample>` и
`RuntimeStateStreamReceiver<TSample>`. Profile владеет quantization и packed
sample encoding. Frame несёт stream type id, tick и отдельный sequence потока;
`RuntimeObjectPatch` и обычный semantic GRC diff здесь не используются.

Client coordinator является единственным wire ingress: он авторизует store,
разрешает profile, декодирует envelope, проверяет header и canonical packed
samples и только после этого публикует profile-validated frame в typed
receiver. Project adapters не разбирают raw wire payload. Connection coalescer
принимает сам profile вместе с его `RuntimeStateStreamLifetime`; retained-state
heartbeat включается только для `EphemeralStreamEntity` и никогда для
`StructuralRuntimeObject`.

Изменение наличия component — structural operation. Для hot component
используются payloadless `AddPresence`/`Remove`; обычные `Add`, `Fields` и
`Custom` несут semantic state и запрещены для `UnreliableState`. Не
переключайте наличие компонента для частого состояния вроде атаки: держите
постоянный `CombatState`-подобный компонент с mode, target, cooldown и sequence.
ECS-only projectile, particle, короткий hit area и временная цель не требуют
GRO, пока у них нет содержательной долговечной идентичности.

Complete-set reconciliation является immutable segmented `Begin..End` cycle и
применяется атомарно только на `End`. Cycle ограничен 10 сегментами / 500 ms при
20 Hz. Interest revoke прерывает старый outgoing cycle и начинает новый по
текущему eligible set. Отклонённый single-frame reconciliation удаляет
provisional buffer и оставляет sequence uncommitted, поэтому exact frame можно
повторить без публикации partial data.

`RuntimeNetworkTelemetry` показывает payload bytes/second по wire stream,
encode time и allocations для preparation, `Pack`/coalescing, canonical
validation и финального wire encoding, dirty components на committed tick,
current projected и last ACKed membership по connection, размеры baseline и
число resync. Существующие coordinators `RuntimeProtocol*` и `RuntimeStoreNetServer` /
`RuntimeStoreNetClient` дают snapshot напрямую, без per-GRC adapters.
Per-connection interest filtering и shadow state сохраняются. Если
метрики подтвердят существенное повторное кодирование, соединения с одинаковым
interest membership можно группировать и кодировать общий payload один раз,
сохранив отдельные delivery/ACK state каждого connection.

## Командная шина

`RuntimeCommandsBus` — это late-update очередь команд.

Механика:

- команда — это `GameRuntimeCommand` с набором runtime-компонентов
- при исполнении bus проходит по компонентам и вызывает `ICommandLogic.Execute(...)`
- сетевой слой при необходимости может перехватить команду через `BeforeExecute`

Преимущество подхода:

- команды используют тот же компонентный язык, что и объекты
- spawn/change logic можно описывать теми же data-oriented примитивами

## Моддинг и внешние asset-паки

`GameAssetLibraryManifest` напрямую монтирует библиотеку asset-ов из файловой структуры:

- корень модов: `Application.persistentDataPath/assets`
- built-in-like мод: `Application.persistentDataPath/assets/base`
- внешние моды: соседние папки с `manifest.json`

Возможности:

- base и external asset-ы резолвятся по одному и тому же `GameAssetKey`
- внешний мод может переопределять base asset
- папки модов индексируются без ScriptableObject-конфига
- asset можно запросить по точной версии или по `latest`

`ModPackage` лениво загружает JSON asset по `GameAssetKey` и восстанавливает нужный `ScriptableObject`.

При старте сессии библиотека захватывается в immutable content snapshot. Изменения файлов на диске не мутируют уже запущенный gameplay catalog; новая ревизия монтируется в следующей сессии.

## Внешний authoring и доставка package

Единица поставки — внешняя папка модуля с каноническими JSON, ресурсами и производным `manifest.json`. Чистая установка получает `base` как внешний content package в `Application.persistentDataPath/assets/base`; он не собирается из Unity-проекта и не импортируется обратно в него.

Контент редактируется напрямую через соседний [DingoGameObjectsCMSEditorServer](../DingoGameObjectsCMSEditorServer/README.md). Его MCP- и Web-клиенты работают со staged `JObject` changeset в смонтированной AppData-библиотеке, валидируют результат, выводят manifest и публикуют изменения с защитой от конфликта по content hash. Цикла build/import через Unity `AssetDatabase` больше нет.

Непосредственные дочерние каталоги корня `assets`, имя которых начинается с `.`, считаются служебными и не монтируются как модули. Поэтому сам корень `assets` может быть рабочим деревом Git: каталог `.git` не попадёт в runtime-discovery.

Unity Editor generators остаются только для checked-in compiled runtime-контрактов: direct-`Type` component table, compact ids, numeric reservations и registry hash. Они компилируют контракты кода и систем, но не создают и не пакуют контент.

## Зависимости

### Прямые зависимости по submodule-ам

| Dependency | Repository | Branch | Why it is needed |
| --- | --- | --- | --- |
| `DingoProjectAppStructure` | `https://github.com/DingoBite/DingoProjectAppStructure.git` | `not pinned in .gitmodules` | `AppModelBase`, app root lifecycle, external dependencies |
| `UnityBindVariables` | `https://github.com/DingoBite/UnityBindVariables` | `not pinned in .gitmodules` | `Bind`, `BindDict`, reactive containers used by `RuntimeStore` and view layer |
| `DingoUnityExtensions` | `https://github.com/DingoBite/DingoUnityExtensions` | `dev` | singletons, pools, view providers, serialization helpers, utils |

Примечание:

- это прямые зависимости самого `DingoGameObjectsCMS`
- другие submodule-и superproject-а могут использоваться интеграционным проектом, но не требуются фреймворку напрямую

### Пакеты и внешние библиотеки

- `Unity.Entities` / `Unity.Collections` — ECS bridge
- `Mirror` — networking layer для `Mirror/`
- `Newtonsoft.Json` — дефолтная сериализация и mod JSON
- `NaughtyAttributes` — editor UX

## Ограничения и trade-offs

- фреймворк сознательно добавляет свой runtime-слой поверх ECS, а не заменяет его
- high-level код должен идти через `RuntimeExecutionContext` / `RS`, а low-level infrastructure всё ещё может работать с explicit realm
- generated serialization schemas нужно держать актуальными и checked-in
- persistent storage service не входит в поставку
- Mirror-слой опционален, но при его использовании нужно соблюдать контракт snapshot/delta
- versioning помогает с shape evolution, но migration policy всё равно должна быть продумана на уровне проекта

## Когда такой подход особенно хорош

Подход особенно полезен, если вам важно хотя бы несколько пунктов из списка:

- asset-driven gameplay
- versioned content pipeline
- общий data model для ECS, сети и persistence
- mod support
- предсказуемая authoritative runtime model
- возможность сериализовать игровой мир как дерево объектов

Если упростить до одной фразы:

> `DingoGameObjectsCMS` превращает `GameAsset` и `GameRuntimeObject` в общий source of truth для runtime state, ECS integration, replication, modding и persistence.




