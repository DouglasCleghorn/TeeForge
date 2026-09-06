const field = document.querySelector('#search');
const results = document.querySelector('#search-results');
let indexPromise;
let revision = 0;
field.addEventListener('input', async () => {
  const current = ++revision;
  const words = field.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
  results.replaceChildren();
  if (!words.length) return;
  try {
    indexPromise ??= fetch('/search-index.json').then(response => {
      if (!response.ok) throw new Error('Search unavailable');
      return response.json();
    });
    const pages = await indexPromise;
    if (current !== revision) return;
    const matches = pages.map(page => {
      const title = page.title.toLowerCase();
      const body = page.text.toLowerCase();
      return { ...page, score: words.every(word => body.includes(word) || title.includes(word))
        ? words.reduce((sum, word) => sum + (title.includes(word) ? 10 : 1), 0) : 0 };
    }).filter(page => page.score).sort((a, b) => b.score - a.score).slice(0, 8);
    if (!matches.length) { results.textContent = 'No matching pages. Try an API name or a shorter phrase.'; return; }
    for (const page of matches) {
      const link = document.createElement('a');
      link.href = page.url;
      link.textContent = `${page.title} · ${page.version}`;
      const description = document.createElement('p');
      const at = Math.max(0, page.text.toLowerCase().indexOf(words[0]) - 55);
      description.textContent = page.text.slice(at, at + 210).replace(/\s+/g, ' ') + '…';
      results.append(link, description);
    }
  } catch {
    if (current === revision) results.textContent = 'Search is unavailable. Use the page links or Markdown index.';
  }
});
document.querySelector('#versions').addEventListener('change', event => {
  window.location.assign(event.target.value);
});
