/**
 * dsh-polish-bridge — a local HTTP bridge that turns a rough draft into a
 * sendable message, using the host's own model.
 *
 * Why a host plugin: the desktop app's model credentials live inside the host
 * (`ctx.llm`), not in a file this tool could read. Calling through `ctx.llm`
 * means no second API key and no second provider configuration.
 *
 * Wire format (loopback only):
 *   GET  /health              -> { ok, port, provider, model, style }
 *   POST /polish { text, style? } -> { ok, text, provider, model, ms }
 *   POST /config { provider, model, style } -> persisted to config.json
 *
 * The caller is a small Windows tray tool that binds a global hotkey, copies the
 * selection, posts it here, and pastes the answer back.
 */

import { createServer } from 'node:http';
import { readFileSync, writeFileSync, mkdirSync, appendFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';

export const name = 'dsh-polish-bridge';
export const inject = [];

const CONFIG_DIR = join(process.env.LOCALAPPDATA ?? homedir(), 'dsh-polish');
const CONFIG_PATH = join(CONFIG_DIR, 'config.json');
const LOG_PATH = join(CONFIG_DIR, 'bridge.log');

const DEFAULTS = { port: 8791, provider: '', model: '', style: 'chat', maxChars: 6000 };

/** The instruction the model gets, one per tone. */
const STYLES = {
  chat: {
    title: '聊天消息',
    body:
      '把用户的口述草稿改写成一条可以直接发给别人的消息：\n' +
      '- 保留原意、事实、数字、人名、时间、链接，不新增也不猜测内容；\n' +
      '- 删掉口头语、重复、跑题，把结论放在最前面；\n' +
      '- 一般 2–5 句，能短则短；原文是长说明时才保留更多句子；\n' +
      '- 用自然的现代汉语，不要公文腔，不要 Markdown，不要表情符号；\n' +
      '- 只输出改写后的正文：不要解释、不要加引号、不要列要点。',
  },
  mail: {
    title: '邮件',
    body:
      '把用户的口述草稿改写成一封得体的中文邮件正文：\n' +
      '- 保留原意、事实、数字、人名、时间、链接，不新增内容；\n' +
      '- 结构：一句话说明来意，必要的背景，明确的请求或结论；\n' +
      '- 语气礼貌但直接，删掉口头语与重复，不写空话；\n' +
      '- 不要写称呼和署名，不要 Markdown 标题；\n' +
      '- 只输出正文，不要解释。',
  },
  brief: {
    title: '要点',
    body:
      '把用户的口述草稿压缩成最精简的要点：\n' +
      '- 保留事实、数字、人名、时间，不新增内容；\n' +
      '- 最多 3 条，每条一行，以「· 」开头；\n' +
      '- 只输出要点本身，不要解释。',
  },
};

function log(...args) {
  try {
    mkdirSync(CONFIG_DIR, { recursive: true });
    const line = `${new Date().toISOString()} ${args
      .map((a) => (typeof a === 'string' ? a : JSON.stringify(a)))
      .join(' ')}\n`;
    appendFileSync(LOG_PATH, line, 'utf8');
  } catch {
    /* logging must never break a request */
  }
}

function readConfig() {
  try {
    if (!existsSync(CONFIG_PATH)) return { ...DEFAULTS };
    return { ...DEFAULTS, ...JSON.parse(readFileSync(CONFIG_PATH, 'utf8')) };
  } catch {
    return { ...DEFAULTS };
  }
}

function writeConfig(next) {
  mkdirSync(CONFIG_DIR, { recursive: true });
  writeFileSync(CONFIG_PATH, JSON.stringify(next, null, 2), 'utf8');
}

/** Resolve a provider/model pair, preferring the cheap DeepSeek route. */
async function resolveRoute(llm, config) {
  if (config.provider !== '' && config.model !== '') {
    return { provider: config.provider, model: config.model };
  }

  const providers = llm.listProviders();
  if (providers.length === 0) throw new Error('no model provider is configured in DSH');

  // Some providers advertise no models at all (they are credential-less shells),
  // so the first one that actually answers is the one to use.
  const ranked = [...providers].sort((a, b) => rankProvider(a) - rankProvider(b));
  const failures = [];

  for (const provider of ranked) {
    let models;
    try {
      models = await llm.listModels(provider.id);
    } catch (cause) {
      failures.push(`${provider.id}: ${cause instanceof Error ? cause.message : String(cause)}`);
      continue;
    }
    if (models.length === 0) {
      failures.push(`${provider.id}: no models`);
      continue;
    }

    const model =
      models.find((m) => /flash/i.test(String(m.id))) ??
      models.find((m) => /chat/i.test(String(m.id))) ??
      models[0];
    return { provider: provider.id, model: model.id };
  }

  throw new Error(`no usable model provider (${failures.join('; ')})`);
}

/** Lower is better: the account-scoped DeepSeek route first, then other DeepSeek shells. */
function rankProvider(provider) {
  const id = String(provider.id ?? '').toLowerCase();
  if (id === 'deepseek-account') return 0;
  if (id === 'deepseek') return 1;
  if (id.includes('deepseek')) return 2;
  return 3;
}

/** One completion through the host's llm service. */
async function complete(llm, route, system, user, signal) {
  const { BlockAssembler, createUserMessage } = await import('@deepseek-ai/dsh-llm');
  const source = { kind: 'plugin:dsh-polish-bridge' };
  const assembler = new BlockAssembler();

  const options = {
    provider: route.provider,
    model: route.model,
    messages: [
      createUserMessage({ content: [{ type: 'text', text: system }], source }),
      createUserMessage({ content: [{ type: 'text', text: user }], source }),
    ],
    maxTokens: 2000,
    ...(signal === undefined ? {} : { signal }),
  };

  for await (const chunk of llm.stream(options)) assembler.push(chunk);

  const finish = assembler.finish;
  if (finish.kind === 'error' || finish.kind === 'aborted') {
    throw new Error(`polish: the model call failed (${finish.failure?.message ?? finish.kind})`);
  }

  const text = assembler
    .blocks()
    .filter((block) => block.type === 'text')
    .map((block) => block.text)
    .join('')
    .trim();

  if (text === '') throw new Error('polish: the model returned nothing');
  return text;
}

function readBody(request, limit) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    request.on('data', (chunk) => {
      size += chunk.length;
      if (size > limit) {
        reject(new Error('request body too large'));
        request.destroy();
        return;
      }
      chunks.push(chunk);
    });
    request.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    request.on('error', reject);
  });
}

function reply(response, status, body) {
  const payload = JSON.stringify(body);
  response.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(payload),
    'cache-control': 'no-store',
  });
  response.end(payload);
}

export function apply(ctx) {
  try {
    const llm = ctx.get('llm');
    if (llm === undefined || llm === null) {
      log('llm service unavailable; bridge not started');
      return;
    }

    const config = readConfig();
    const state = { ...config };

    const server = createServer(async (request, response) => {
    try {
      const url = new URL(request.url ?? '/', 'http://127.0.0.1');

      if (request.method === 'GET' && url.pathname === '/health') {
        reply(response, 200, {
          ok: true,
          port: state.port,
          provider: state.provider || '(auto)',
          model: state.model || '(auto)',
          style: state.style,
        });
        return;
      }

      if (request.method === 'POST' && url.pathname === '/config') {
        const body = JSON.parse((await readBody(request, 8192)) || '{}');
        for (const key of ['provider', 'model', 'style']) {
          if (typeof body[key] === 'string') state[key] = body[key];
        }
        if (!STYLES[state.style]) state.style = DEFAULTS.style;
        writeConfig({ port: state.port, provider: state.provider, model: state.model, style: state.style, maxChars: state.maxChars });
        reply(response, 200, { ok: true, ...state });
        return;
      }

      if (request.method === 'POST' && url.pathname === '/polish') {
        const started = Date.now();
        const raw = await readBody(request, 256 * 1024);
        const body = JSON.parse(raw || '{}');
        const text = typeof body.text === 'string' ? body.text.trim() : '';
        if (text === '') {
          reply(response, 400, { ok: false, error: 'empty text' });
          return;
        }
        if (text.length > state.maxChars) {
          reply(response, 413, { ok: false, error: `text longer than ${state.maxChars} characters` });
          return;
        }

        const style = STYLES[body.style] ? body.style : STYLES[state.style] ? state.style : 'chat';
        const route = await resolveRoute(llm, state);
        const answer = await complete(llm, route, STYLES[style].body, text);

        log('polish', `${text.length}ch ->`, `${answer.length}ch`, route.model, `${Date.now() - started}ms`);
        reply(response, 200, {
          ok: true,
          text: answer,
          style,
          provider: route.provider,
          model: route.model,
          ms: Date.now() - started,
        });
        return;
      }

      reply(response, 404, { ok: false, error: 'not found' });
    } catch (cause) {
      const message = cause instanceof Error ? cause.message : String(cause);
      log('error', message);
      reply(response, 500, { ok: false, error: message });
    }
  });

    server.on('error', (cause) => log('server error', cause.message));
    server.listen(state.port, '127.0.0.1', () => log('listening', state.port));

    ctx.on?.('dispose', () => {
      server.close();
      log('disposed');
    });
  } catch (cause) {
    log('apply failed', cause instanceof Error ? cause.message : String(cause));
  }
}
