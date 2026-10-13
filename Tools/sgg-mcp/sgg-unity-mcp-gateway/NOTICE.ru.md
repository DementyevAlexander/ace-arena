# NOTICE

Этот репозиторий лицензирован по **Stinger Royalty-Free EULA 1.0**.

**SPDX-License-Identifier:** `LicenseRef-Stinger-Royalty-Free-EULA-1.0`  
**Основная версия лицензии:** русский текст в `LICENSE.ru.md`  
**Английский справочный текст:** `LICENSE.md`, для удобства чтения  
**Лицензиар:** ROMAN ILYIN  
**Название проекта / Актива:** sgg-unity-mcp-gateway  
**Канонический репозиторий:** https://github.com/romanilyin/sgg-unity-mcp-gateway  
**Copyright:** Copyright © 2026 ROMAN ILYIN. Все права защищены, кроме прав, прямо предоставленных EULA.

Русская версия EULA является основной и имеет преимущественную силу. При расхождении, противоречии или различии толкования между русской и английской версиями применяется русская версия.

## Лицензируемые материалы

Лицензируемый Актив включает исходный код sgg-unity-mcp-gateway, Unity-пакеты, компоненты MCP gateway, MCP-модули, определения MCP-команд, схемы, манифесты, адаптеры, конфигурационные файлы, документацию, samples, тесты и связанные материалы.

Настоящий EULA применяется ко всем файлам в этом репозитории, если файл, папка, зависимость, пакет или уведомление третьей стороны не указывает иное.

## Обязательное уведомление об атрибуции

Любое распространение этого Актива, Производного актива или Конечного продукта, содержащего этот Актив, должно сохранять применимые уведомления, требуемые EULA. Используйте следующее уведомление об атрибуции там, где обычно размещаются уведомления третьих лиц:

```text
Этот продукт включает sgg-unity-mcp-gateway или основан на нём.
Оригинальный репозиторий: https://github.com/romanilyin/sgg-unity-mcp-gateway
Copyright © 2026 ROMAN ILYIN.
Лицензировано по Stinger Royalty-Free EULA 1.0.
```

## Изменённые версии

Изменённые standalone-форки, бесплатные форки и Производные активы также должны включать:

```text
Это неофициальная изменённая версия. Она не одобрена, не спонсируется,
не утверждена, не сертифицирована, не сопровождается и не поддерживается ROMAN ILYIN.
```

## Резюме

Бесплатно для личных, внутренних, открытых и коммерческих Конечных продуктов. Без роялти. Можно создавать и распространять Stinger-compatible MCP-расширения и MCP-наборы команд для своих ассетов, tools, игр, сервисов, workflow, клиентских поставок и внутренних пайплайнов. Нельзя продавать сам gateway, производный gateway, MCP module framework или command-pack infrastructure как standalone/competing product.

Это резюме приведено только для удобства. Полные условия лицензии находятся в `LICENSE.md`.

## Уведомление о MCP-расширениях

Этот репозиторий содержит MCP gateway, MCP-модули, определения MCP-команд, схемы, манифесты, адаптеры, конфигурационные файлы или связанные MCP integration components.

EULA разрешает третьим лицам создавать и распространять Stinger-compatible MCP-расширения и Наборы MCP-команд для своих собственных ассетов, инструментов, игр, приложений, сервисов, workflows, asset-пакетов, клиентских поставок и внутренних пайплайнов, включая коммерческие Конечные продукты, при условии, что такие расширения и наборы команд преимущественно поддерживают их собственный оригинальный ассет/продукт и не являются отдельной перепродажей, отдельной коммерциализацией, replacement forks или конкурирующими версиями этого Актива.

## Уведомления третьих лиц

Полные сгенерированные уведомления для bundled runtime libraries и сохранённых сторонних материалов вне npm находятся в `THIRD_PARTY_NOTICES.md`. Детерминированный software bill of materials в формате CycloneDX 1.6 находится в `sbom.cdx.json`. Копии обоих файлов включены в Unity Gateway runtime. Stinger Royalty-Free EULA не заменяет и не отменяет лицензии третьих лиц.

Bundled fallback font `unity/Packages/com.sungeargames.unity-mcp/Editor/UI/Fonts/NotoSans-Regular.ttf` взят из проекта Noto Sans by The Noto Project Authors и licensed under the SIL Open Font License, Version 1.1. Полный текст лицензии включен в `unity/Packages/com.sungeargames.unity-mcp/Editor/UI/Fonts/NotoSans-OFL.txt`.

Логика SerializeReference YAML scanning в `unity/Packages/com.sungeargames.unity-mcp/Editor/Commands/SerializeReferenceCommands.cs` частично основана на идеях и parsing behavior из `SerializeReferenceDropdownIntegration` by Alexey Taranov, licensed under the MIT License.

```text
MIT License

Copyright (c) 2026 Alexey Taranov

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
