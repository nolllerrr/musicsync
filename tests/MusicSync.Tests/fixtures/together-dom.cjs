'use strict';

// Small DOM fixture for the injected renderer's Node tests. It models node identity,
// focus, events and sidebar replacement; CSS/layout need a real Electron check.
class Node {
  constructor(tagName, ownerDocument) {
    this.tagName = tagName;
    this.ownerDocument = ownerDocument;
    this.children = [];
    this.attributes = new Map();
    this.listeners = new Map();
    this.dataset = {};
    this.style = {};
    this.value = '';
    this.disabled = false;
    this.scrollTop = 0;
    this.text = '';
  }

  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  get hidden() { return this.attributes.has('hidden'); }
  set hidden(value) { if (value) this.setAttribute('hidden', ''); else this.attributes.delete('hidden'); }
  get classList() { return (this.getAttribute('class') ?? '').split(' ').filter(Boolean); }
  get textContent() { return this.text + this.children.map(node => node.textContent).join(''); }
  set textContent(value) {
    for (const child of this.children) child.parentElement = undefined;
    this.children = [];
    this.text = String(value);
  }
  get isConnected() { return this === this.ownerDocument.body || Boolean(this.parentElement?.isConnected); }
  get nextElementSibling() {
    return this.parentElement?.children[this.parentElement.children.indexOf(this) + 1] ?? null;
  }
  append(...nodes) { for (const node of nodes) this.insertBefore(node, null); }
  insertBefore(node, reference) {
    if (node === reference) return;
    node.remove();
    const index = reference ? this.children.indexOf(reference) : this.children.length;
    if (index < 0) throw new Error('Reference must be a child');
    this.children.splice(index, 0, node);
    node.parentElement = this;
  }
  before(node) { this.parentElement.insertBefore(node, this); }
  remove() {
    if (!this.parentElement) return;
    this.parentElement.children.splice(this.parentElement.children.indexOf(this), 1);
    this.parentElement = undefined;
  }
  contains(node) { return node === this || this.children.some(child => child.contains(node)); }
  matches(selector) {
    const [, tag, id, attribute, value] = /^(\w+)?(?:#([\w-]+))?(?:\[([\w-]+)(?:="([^"]*)")?\])?$/.exec(selector) ?? [];
    return Boolean(tag || id || attribute) && (!tag || this.tagName === tag) &&
      (!id || this.getAttribute('id') === id) && (!attribute ||
        (value === undefined ? this.attributes.has(attribute) : this.getAttribute(attribute) === value));
  }
  querySelectorAll(selector) {
    return this.children.flatMap(child => [...(child.matches(selector) ? [child] : []), ...child.querySelectorAll(selector)]);
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
  closest(selector) { return this.matches(selector) ? this : this.parentElement?.closest(selector) ?? null; }
  addEventListener(name, listener) {
    if (!this.listeners.has(name)) this.listeners.set(name, []);
    this.listeners.get(name).push(listener);
  }
  async dispatch(name, values = {}) {
    const event = { target: this, preventDefault() {}, stopPropagation() {}, ...values };
    for (const listener of this.listeners.get(name) ?? []) await listener(event);
    // run() is intentionally fire-and-forget in the renderer's event handlers.
    await Promise.resolve();
  }
  focus() { this.ownerDocument.activeElement = this; }
  getBoundingClientRect() { return { right: 200, top: 100 }; }
  get offsetWidth() { return 336; }
  get offsetHeight() { return 400; }
}

function createDom() {
  const document = new Node('document');
  document.ownerDocument = document;
  document.body = new Node('body', document);
  document.children.push(document.body);
  document.createElement = tag => new Node(tag, document);
  document.createElementNS = (_namespace, tag) => document.createElement(tag);
  document.getElementById = id => document.querySelector(`#${id}`);
  function sidebar() {
    const nav = document.createElement('aside');
    nav.setAttribute('data-test-id', 'NAVBAR');
    const list = document.createElement('ul');
    for (const name of ['HOME', 'FOR_YOU_AND_TRENDS']) {
      const item = document.createElement('li');
      const link = document.createElement('a');
      link.setAttribute('data-test-id', `NAVBAR_NAVIGATION_ITEM_${name}`);
      const iconContainer = document.createElement('div');
      iconContainer.append(document.createElement('svg'));
      const textContainer = document.createElement('div');
      const label = document.createElement('span');
      label.setAttribute('title', name);
      textContainer.append(label);
      link.append(iconContainer, textContainer);
      item.append(link);
      list.append(item);
    }
    nav.append(list);
    document.body.append(nav);
    return nav;
  }
  sidebar();
  return { document, sidebar };
}

module.exports = { createDom };
