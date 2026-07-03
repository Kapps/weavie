# Heading one {color="red"}
## Heading two {color="orange_bg"}
### Heading three
## Toggle heading {toggle="true" color="purple"}
	Hidden child paragraph.
	- Hidden bullet
Plain paragraph with **bold**, *italic*, ~~strike~~, `code`, [link](https://example.com), $E=mc^2$, <span underline="true">underlined</span>, <span color="green">green text</span>, <span color="yellow_bg">highlighted</span>, and a line<br>break.
	A tab-nested child paragraph under the paragraph above.
- Bullet one {color="blue"}
	- Nested bullet
		1. Deep numbered item
	Paragraph child of bullet one.
- Bullet two
1. Numbered one
	1. Numbered nested
	- Bullet under numbered
2. Numbered two
- [ ] Unchecked task {color="gray"}
	- [x] Nested checked task
	Task child paragraph.
- [x] Checked task
> Quoted rich text {color="brown_bg"}
	Quote child paragraph.
> Line 1<br>Line 2<br>Line 3
<details color="pink">
<summary>Toggle title</summary>
	Toggle child one.
	<callout icon="⚠️">
		Nested callout inside a toggle.
	</callout>
</details>
<callout icon="💡" color="gray_bg">
	Callout body with **marks**.
	- A list inside the callout
</callout>
```mermaid
graph TD; A-->B;
```
$$
\int_0^1 x^2 dx
$$
<table fit-page-width="false" header-row="true" header-column="false">
	<colgroup>
		<col color="blue_bg"/>
		<col/>
	</colgroup>
	<tr color="gray_bg">
		<td>Name</td>
		<td>Value</td>
	</tr>
	<tr>
		<td color="red">Alpha</td>
		<td>1</td>
	</tr>
</table>
| GFM | Table |
|---|---|
| pipe | cells |
---
<empty-block/>
<columns>
	<column>
		Left column text.
	</column>
	<column>
		Right column text.
	</column>
</columns>
![A caption](https://example.com/img.png) {color="blue_bg"}
<audio src="https://example.com/a.mp3" color="gray">Audio caption</audio>
<video src="https://example.com/v.mp4">Video caption</video>
<file src="https://example.com/f.zip">File caption</file>
<pdf src="https://example.com/d.pdf">PDF caption</pdf>
<page url="https://www.notion.so/Sub-page-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d">Sub page</page>
<database url="https://www.notion.so/1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6e" inline="true" icon="📚">Tasks DB</database>
<table_of_contents color="gray"/>
<synced_block url="https://www.notion.so/1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6f">
	Synced content paragraph.
</synced_block>
<synced_block_reference url="https://www.notion.so/1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6f">
	Referenced synced content.
</synced_block_reference>
<unknown url="https://www.notion.so/1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c70" alt="link_preview"/>
Mentions: <mention-user url="{{user://abc}}">Ada Lovelace</mention-user> and <mention-page url="https://www.notion.so/Other-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c71">Other page</mention-page> and <mention-date start="2026-07-01" end="2026-07-04"/> and <mention-date start="2026-07-02" startTime="09:00" timeZone="America/New_York"/>.
Custom emoji :party_blob: and a citation.[^https://example.com/source]
Escapes: \*not bold\*, \<not a tag\>, \{not attrs\}, \$not math\$, a\|pipe, up\^caret, \~tilde\~, \`tick\`, \[brackets\].
A literal trailing brace block {note} stays text.
