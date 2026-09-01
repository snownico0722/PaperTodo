# Markdown Skin Demo
This note covers the Markdown styles currently handled by PaperTodo's AvalonEdit skin.

**Bold text** and __alternate bold__.
*Italic text* and _alternate italic_.
~~Strikethrough text~~.
`inline code` keeps a code background.
A link label is highlighted: [PaperTodo](https://github.com/testsnow0722/todoc).
---
### Lists and tasks
- Bullet item with **bold** text.
- [ ] Open task marker.
- [x] Completed task marker.
     * Asterisk bullet marker.
+ Plus bullet marker.
1. Ordered list with `inline code`.
2) Ordered list using a parenthesis marker.

#### Quote
> Quote lines draw a left rail and muted text.
> They keep the same wrapping and scroll metrics as edit mode.

##### Fenced code
```csharp
public static int Add(int left, int right)
{
    return left + right;
}
```
~~~~
Plain fenced block with tildes.
~~~~