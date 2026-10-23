(() => {
  "use strict";

  const data = window.IMAGE_TEST_DATA;
  const rows = document.getElementById("rows");
  const summary = document.getElementById("summary");
  const search = document.getElementById("search");
  const filter = document.getElementById("filter");
  const showLuna = document.getElementById("show-luna");
  const showSol = document.getElementById("show-sol");
  const dialog = document.getElementById("detail");

  if (!data || !Array.isArray(data.samples)) {
    summary.textContent = "Classification data is missing. Generate data.js with build_data.py.";
    return;
  }

  const byModel = {
    luna: new Map(data.luna.map(result => [result.id, result])),
    sol: new Map(data.sol.map(result => [result.id, result]))
  };

  function text(tag, content, className) {
    const element = document.createElement(tag);
    element.textContent = content;
    if (className) element.className = className;
    return element;
  }

  function headDifference(a, b) {
    if (!a.head && !b.head) return false;
    if (!a.head || !b.head) return true;
    return Math.hypot(a.head.x - b.head.x, a.head.y - b.head.y) > 0.12;
  }

  function differences(a, b) {
    const fields = ["format", "compound", "animal_count", "same_kind", "people", "artifacts", "background"];
    const result = fields.filter(field => a[field] !== b[field]);
    if (headDifference(a, b)) result.push("head");
    return result;
  }

  function imageFrame(sample, a, b) {
    const frame = text("span", "", "image-frame");
    const image = document.createElement("img");
    image.src = sample.image_url;
    image.alt = sample.filename;
    image.loading = "lazy";
    frame.append(image);
    for (const [model, result, visible] of [["luna", a, showLuna.checked], ["sol", b, showSol.checked]]) {
      if (!visible || !result.head) continue;
      const marker = text("span", model === "luna" ? "L" : "S", `marker ${model}`);
      marker.style.left = `${result.head.x * 100}%`;
      marker.style.top = `${result.head.y * 100}%`;
      marker.title = `${model}: head at ${Math.round(result.head.x * 100)}%, ${Math.round(result.head.y * 100)}%`;
      marker.setAttribute("aria-hidden", "true");
      frame.append(marker);
    }
    return frame;
  }

  function resultCell(result) {
    const cell = text("td", "", "model-result");
    const facts = document.createElement("dl");
    facts.className = "facts";
    const entries = [
      ["Format", result.format],
      ["Compound", result.compound ? "yes" : "no"],
      ["Animals", result.animal_count ?? "uncertain"],
      ["Same kind", result.same_kind],
      ["People", result.people],
      ["Artifacts", result.artifacts],
      ["Background", result.background],
      ["Head", result.head ? `${Math.round(result.head.x * 100)}%, ${Math.round(result.head.y * 100)}%` : "not identified"],
      ["Confidence", result.confidence]
    ];
    for (const [label, value] of entries) {
      facts.append(text("dt", label), text("dd", String(value)));
    }
    cell.append(facts, text("p", result.note));
    return cell;
  }

  function openDetail(sample, a, b) {
    document.getElementById("detail-title").textContent = `${sample.id} · ${sample.filename}`;
    document.getElementById("detail-image").replaceChildren(imageFrame(sample, a, b));
    dialog.showModal();
  }

  function render() {
    const query = search.value.trim().toLocaleLowerCase();
    const selected = filter.value;
    const fragment = document.createDocumentFragment();
    let visible = 0;

    for (const sample of data.samples) {
      const a = byModel.luna.get(sample.id);
      const b = byModel.sol.get(sample.id);
      const diff = differences(a, b);
      if (query && !sample.filename.toLocaleLowerCase().includes(query) && !sample.id.includes(query)) continue;
      if (selected === "disagreements" && diff.length === 0) continue;
      if (selected === "head-differences" && !diff.includes("head")) continue;
      if (selected === "uncertain" && a.confidence !== "low" && b.confidence !== "low" && a.format !== "uncertain" && b.format !== "uncertain") continue;
      visible++;

      const row = document.createElement("tr");
      row.append(text("td", sample.id));
      const preview = document.createElement("td");
      const button = document.createElement("button");
      button.type = "button";
      button.className = "image-button";
      button.setAttribute("aria-label", `Enlarge image ${sample.id}`);
      button.append(imageFrame(sample, a, b));
      button.addEventListener("click", () => openDetail(sample, a, b));
      preview.append(button);
      const caption = text("span", "", "filename");
      const source = document.createElement("a");
      source.href = `https://en.wikipedia.org/wiki/File:${encodeURIComponent(sample.filename.replaceAll(" ", "_"))}`;
      source.target = "_blank";
      source.rel = "noopener noreferrer";
      source.textContent = sample.filename;
      caption.append(source);
      preview.append(caption);
      row.append(preview, resultCell(a), resultCell(b));
      const flags = document.createElement("td");
      if (diff.length === 0) flags.append(text("span", "Agreement", "flag"));
      else for (const field of diff) flags.append(text("span", field, field === "head" ? "flag warn" : "flag"));
      row.append(flags);
      fragment.append(row);
    }

    rows.replaceChildren(fragment);
    const totalDisagreements = data.samples.filter(sample => differences(byModel.luna.get(sample.id), byModel.sol.get(sample.id)).length > 0).length;
    const headDisagreements = data.samples.filter(sample => headDifference(byModel.luna.get(sample.id), byModel.sol.get(sample.id))).length;
    summary.replaceChildren(
      text("span", `${visible} shown / ${data.samples.length} sampled`),
      text("span", `${totalDisagreements} with label or head differences`),
      text("span", `${headDisagreements} with head differences`)
    );
  }

  for (const control of [search, filter, showLuna, showSol]) {
    control.addEventListener(control === search ? "input" : "change", render);
  }
  document.getElementById("close-detail").addEventListener("click", () => dialog.close());
  render();
})();
