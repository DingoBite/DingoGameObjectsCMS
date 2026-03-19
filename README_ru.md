# DingoGameObjectsCMS

`DingoGameObjectsCMS` — это content-first runtime framework для Unity, в котором игровое поведение описывается versioned asset-ами, во время выполнения собирается в дерево runtime-объектов, а затем при необходимости материализуется в ECS, сеть, view и persistent data.

Идея фреймворка простая:

1. Вы описываете объект не кодом сцены, а `GameAsset`.
2. `GameAsset` собирает `GameRuntimeObject` с набором `GameRuntimeComponent`.
3. `GameRuntimeObject` живёт в `RuntimeStore`, который умеет хранить дерево объектов, отслеживать dirty-изменения и публиковать потоки изменений.
4. Тот же runtime-слой может быть использован для:
   - построения ECS entity;
   - сетевой репликации;
   - команд;
   - моддинга;
   - persistent data.

Это не просто “CMS для ScriptableObject”, а унифицированная модель игры, где asset pipeline, runtime model, ECS bridge, replication и modding используют один и тот же язык данных.

## Почему это решение полезно

- **Content-first архитектура.** Игра описывается asset-ами и runtime-компонентами, а не разрастающимся набором scene-specific MonoBehaviour.
- **Версионность как часть модели.** Asset имеет `GameAssetKey` и `GUID`, поэтому изменение shape данных можно оформлять новой версией asset-а, а не ломать старые сейвы и профили.
- **Одна модель для нескольких слоёв.** Тот же runtime-объект может стать ECS entity, network payload, persistent root object или mod asset.
- **Явный runtime store.** Состояние игры живёт не “размазано по сцене”, а в деревьях `RuntimeStore`, которые легко сериализовать, синхронизировать и анализировать.
- **Dirty-by-design.** Store копит структурные и компонентные изменения и публикует их как отдельные потоки, поэтому нет необходимости каждый раз пересылать весь мир.
- **Слабая связность сериализации и сети.** Runtime serialization вынесена в `IRuntimePayloadSerializer`, а Mirror работает поверх этой абстракции.
- **Mod-friendly pipeline.** Встроенные asset-ы и внешние mod pack-ы используют одинаковые ключи, одинаковую сериализацию и единый резолвер.
- **Asset-backed persistent data.** Тот же подход можно применять к настройкам, профилям, мета-прогрессии и сейвам, сохраняя shape данных в versioned asset-ах.

## Ключевые концепции

### `GameAssetKey`

`GameAssetKey` состоит из:

- `Mod`
- `Type`
- `Key`
- `Version`

Канонический layout asset-каталога:

```text
Assets/GameAssets/<mod>/<type>/<key>/<key>@<version>.asset
```

Пример:

```text
Assets/GameAssets/base/characters/player/player@1.2.0.asset
```

Правило резолва версии:

- `version == null` означает точный запрос к `0.0.0`
- `version == ""` или пробельная строка означает запрос `latest`
- `latest` выбирается по максимальной доступной semver внутри того же `(mod, type, key)`

Это даёт удобный компромисс:

- код может жёстко запросить конкретную shape-версию;
- интеграционный слой может запросить “самый новый совместимый asset”.

### `GameAssetScriptableObject`

Базовый `ScriptableObject` фреймворка. Содержит:

- `GameAssetKey`
- уникальный `GUID`

`GUID` идентифицирует конкретный asset/version instance. Это отдельная сущность относительно `GameAssetKey`.

### `GameAsset`

`GameAsset` — это versioned description объекта. Он хранит список `GameAssetComponent` и умеет:

- собирать `GameRuntimeObject` через `SetupRuntimeObject(...)`
- собирать `GameRuntimeCommand` через `CreateRuntimeCommand()`

Именно здесь asset-модель превращается в runtime-модель.

### `GameRuntimeObject`

`GameRuntimeObject` — базовый runtime-узел дерева. Он хранит:

- `Key`
- `AssetGUID`
- `SourceAssetGUID`
- список `GameRuntimeComponent`
- `InstanceId`
- `StoreId`
- `Realm`

Также он умеет:

- добавлять и заменять runtime-компоненты
- отслеживать dirty-изменения по данным и по структуре компонентов
- создавать ECS entity через `CreateEntity(...)`

`SourceAssetGUID` нужен для source/presentation linkage и смежных runtime-кейсов. Он не используется как lineage версии.

### `GameRuntimeComponent`

`GameRuntimeComponent` — базовый класс runtime-компонента. Он определяет runtime shape объекта и при необходимости добавляет ECS-компоненты/буферы в `SetupForEntity(...)`.

Это важная граница:

- если компонент только хранит данные, он может вообще не участвовать в ECS;
- если компонент нужен для simulation, он добавляет нужные ECS-компоненты;
- если компонент должен уходить в dirty/sync, он реализует соответствующие dirty-маркеры.

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

### Dirty model

Ключевые маркеры:

- `IStoreDataDirty` — изменения данных компонента должны попадать в dirty-репликацию
- `IStoreStructDirtyIgnore` — структурные изменения такого компонента игнорируются

Практический смысл:

- вы явно контролируете, что уходит в delta;
- компоненты, важные только локально, не обязаны шуметь в сети;
- структура store-а и данные компонентов живут как разные каналы изменений.

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

### Asset -> Runtime -> View

View-слой может подписываться на runtime-объекты через `GameRuntimeObjectView` и `GameRuntimeObjectsCollection`, не ломая separation между данными и отображением.

### Asset -> Runtime -> Network

Сеть синхронизирует не ECS напрямую, а `RuntimeStore`. Это даёт:

- одинаковую модель для authoritative state и replication
- предсказуемую сериализацию
- возможность делать full snapshot и delta на одном и том же слое

### Asset -> Runtime -> Persistence

Фреймворк хорошо подходит и для persistent data:

- `settings`
- `profiles`
- `metas`
- `saves`

Рекомендуемый паттерн:

- отделять persistent stores от gameplay stores
- задавать shape persistent-объектов через `GameAsset`, а не ручной `new ...`
- падать fail-fast, если нужный bootstrap asset не задан
- решать несовместимые изменения через новую версию asset-а или migration code

В самом `DingoGameObjectsCMS` нет готового disk/cloud persistence service, но все базовые примитивы для asset-backed persistent data уже есть.

## Сериализация

Сериализация построена вокруг абстракции `IRuntimePayloadSerializer`.

Что это даёт:

- runtime-слой не зависит от конкретного формата
- Mirror не является владельцем сериализации
- текущий JSON можно позже заменить на бинарный или другой оптимизированный формат

Текущее состояние:

- дефолтный serializer — `JsonRuntimePayloadSerializer`
- глобальная точка подмены — `RuntimePayloadSerialization`
- для runtime-компонентов используется manifest type id

Обязательный runtime artifact:

```text
Assets/StreamingAssets/runtime_component_types.json
```

Он нужен для:

- сетевой репликации runtime-компонентов
- десериализации runtime-компонентов по `compTypeId`

Если вы добавили новый `GameRuntimeComponent`, manifest нужно регенерировать через `Tools/Runtime Types/Generate Manifest` или через build preprocess.

## Сетевая синхронизация

Сетевой слой построен поверх Mirror, но синхронизирует `RuntimeStore`.

Серверная часть:

- подписывается на dirty-события store-ов
- собирает full snapshot или delta payload
- отправляет snapshot при готовности клиента
- ждёт `Ack`
- умеет инициировать resync

Клиентская часть:

- принимает sync message
- десериализует payload
- применяет snapshot/delta через `RuntimeStoreSnapshotCodec.ApplySync`
- подтверждает применение через `Ack`
- при ошибке запрашивает полный resync

Поддерживаются:

- `FullSnapshot`
- `DeltaTick`

Идея в том, что gameplay-сеть смотрит на runtime world как на сериализуемую структуру, а не как на набор произвольных MonoBehaviour.

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

`GameAssetLibraryManifest` умеет собирать библиотеку asset-ов из двух источников:

- built-in assets внутри проекта
- внешние mod pack-ы с `manifest.json`

Возможности:

- built-in и external asset-ы резолвятся по одному и тому же `GameAssetKey`
- внешний мод может переопределять built-in asset
- mount point имеет приоритет
- asset можно запросить по точной версии или по `latest`

`ModPackage` лениво загружает JSON asset по `GameAssetKey` и восстанавливает нужный `ScriptableObject`.

Это делает моддинг не отдельной подсистемой поверх игры, а продолжением того же asset pipeline.

## Editor tooling

В комплекте есть редакторские инструменты для asset-пайплайна:

- `GameAssetKeyRebuilder`
  - работает только с каноническим layout
  - синхронизирует `_key` и путь asset-а
- `GameAssetVersioningTools`
  - дублирует выбранный versioned asset
  - повышает semver
  - генерирует новый GUID
- `ModBuilder`
  - экспортирует мод в JSON + `manifest.json`
- `ModImporter`
  - импортирует JSON-мод обратно в Unity asset-ы
- `SubAssetFixer`
  - пересобирает sub-assets после импорта
- `RuntimeComponentTypeManifestGenerator`
  - обновляет manifest runtime component type id

Эти инструменты нужны не только для удобства редактора. Они поддерживают главный контракт системы: asset shape, versioning, serialization и runtime reconstruction должны совпадать.

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
- serialization manifest нужно держать в актуальном состоянии
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

> `DingoGameObjectsCMS` превращает `GameAsset` в единый source of truth для runtime state, ECS integration, replication, modding и asset-backed persistence.
