import DOMPurify from "dompurify";

// GitHub's own HTML allowlist for user content, so an untrusted author can't restyle, cover, or script the app.
const ALLOWED_TAGS = words(`
  h1 h2 h3 h4 h5 h6 h7 h8 br b i strong em a pre code img tt div ins del sup sub p ol ul table thead tbody
  tfoot tr td th blockquote dl dt dd kbd q samp var hr ruby rt rp li s strike summary details caption
  figure figcaption abbr bdo cite dfn mark small span time wbr input`);
const ALLOWED_ATTR = words(`
  href src alt longdesc cite abbr align axis border cellpadding cellspacing char charoff checked class
  clear colspan color compact coords datetime dir disabled frame headers height hreflang hspace ismap label
  lang media nohref noshade nowrap open rel rev role rows rowspan rules scope shape size span start summary
  title type usemap valign vspace width`);

export function sanitizeGithubHtml(html: string): DocumentFragment {
  const fragment = DOMPurify.sanitize(html, {
    ALLOWED_TAGS,
    ALLOWED_ATTR,
    ALLOW_DATA_ATTR: false,
    RETURN_DOM_FRAGMENT: true,
  });
  // Images load straight from the author's host (GitHub proxies them; we can't), so only over https.
  for (const image of fragment.querySelectorAll("img")) {
    if (!/^https:\/\//i.test(image.getAttribute("src") ?? "")) {
      image.removeAttribute("src");
    }
  }
  for (const input of fragment.querySelectorAll("input")) {
    if (input.type === "checkbox") {
      input.disabled = true;
    } else {
      input.remove();
    }
  }
  return fragment;
}

function words(list: string): string[] {
  return list.trim().split(/\s+/);
}
