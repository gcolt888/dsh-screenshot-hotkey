/**
 * Client half of the screenshot bundle.
 *
 * The whole editing surface lives in the native window (shot.exe): it freezes
 * the desktop, the user drags a region, annotates it in place and presses 完成.
 * The page only receives the finished PNG and hands it to the composer.
 *
 * 两条触发路径，刻意分开：
 * 1. DSH 在前台 → 走 DSH 官方快捷键服务（快捷键面板里能改键，不抢窗口焦点）。
 * 2. DSH 不在前台 → 热键小程序直接拉起 shot.exe 截屏，**不激活 DSH**，
 *    截完把 PNG 放进队列表；这里的常驻长轮询取到图后自动贴进输入框。
 *    这样按下热键时看到的还是你要截的那个窗口，不会被 DSH 盖住。
 */
window.__ModuleLoader__.load({
  id: 'dsh-screenshot-hotkey',
  factory(require) {
    const React = require('react')
    const h = React.createElement
    const { useState, useEffect } = React

    const CAPTURE_URL = '/shot/capture'
    const PENDING_URL = '/shot/pending'

    let busy = false
    let catalogStore = null

    /**
     * A paste event is the one public door into the composer's attachment
     * pipeline: the draft mints its own attachment ids during that flow, so no
     * plugin can inject one from outside.
     */
    function pasteIntoComposer(blob) {
      const target = document.querySelector('[contenteditable="true"]')
      if (!target) return false
      const file = new File([blob], `shot-${Date.now()}.png`, { type: 'image/png' })
      const transfer = new DataTransfer()
      transfer.items.add(file)
      target.focus()
      target.dispatchEvent(new ClipboardEvent('paste', {
        clipboardData: transfer,
        bubbles: true,
        cancelable: true,
      }))
      return true
    }

    async function deliver(blob) {
      if (pasteIntoComposer(blob)) return true
      try {
        if (navigator.clipboard && window.ClipboardItem) {
          await navigator.clipboard.write([new window.ClipboardItem({ 'image/png': blob })])
          window.alert('截图已复制到剪贴板（没找到输入框），按 Ctrl+V 粘贴即可')
          return false
        }
      } catch {
        /* clipboard is a courtesy, not the transport */
      }
      window.alert('截图插入失败：找不到输入框')
      return false
    }

    /** DSH 在前台时的路径：由宿主打开选择器，再把结果交给页面。 */
    async function captureViaHost() {
      if (busy) return
      busy = true
      const previousCursor = document.documentElement.style.cursor
      document.documentElement.style.cursor = 'progress'
      let blob = null
      try {
        const response = await fetch(CAPTURE_URL, { headers: { 'x-dsh-shot': '1' }, cache: 'no-store' })
        if (response.status === 204) return
        const payload = await response.json().catch(() => ({}))
        if (!response.ok || !payload.dataUrl) {
          throw new Error(payload.error || `抓屏失败 HTTP ${response.status}`)
        }
        blob = await (await fetch(payload.dataUrl)).blob()
        if (!pasteIntoComposer(blob)) throw new Error('找不到输入框')
      } catch (error) {
        let note = ''
        try {
          if (blob && navigator.clipboard && window.ClipboardItem) {
            await navigator.clipboard.write([new window.ClipboardItem({ 'image/png': blob })])
            note = '（已复制到剪贴板，可按 Ctrl+V 粘贴）'
          }
        } catch {
          /* clipboard is a courtesy, not the transport */
        }
        window.alert(`截图插入失败：${String((error && error.message) || error)}${note}`)
      } finally {
        document.documentElement.style.cursor = previousCursor
        busy = false
      }
    }

    /**
     * DSH 不在前台时的路径：热键小程序已经截好图放进队列，这里长轮询取走。
     * 长轮询（宿主最多挂 20 秒）比定时器省，也不会有明显延迟。
     */
    async function drainExternalCaptures(controller) {
      while (!controller.signal.aborted) {
        try {
          const response = await fetch(`${PENDING_URL}?wait=20000`, {
            cache: 'no-store',
            signal: controller.signal,
          })
          if (response.status === 204) continue
          const payload = await response.json().catch(() => null)
          if (!payload || !payload.dataUrl || busy) continue
          busy = true
          try {
            const blob = await (await fetch(payload.dataUrl)).blob()
            await deliver(blob)
          } finally {
            busy = false
          }
        } catch (error) {
          if (controller.signal.aborted) return
          await new Promise((resolve) => setTimeout(resolve, 2000))
        }
      }
    }

    /** 当前生效的键位文案（用户在快捷键面板改过就跟着变）。 */
    function readKeys() {
      try {
        const rows = catalogStore?.getSnapshot?.() ?? []
        const row = rows.find((entry) => entry.id === 'dsh-screenshot.capture')
        return row && row.keys ? row.keys : ''
      } catch {
        return ''
      }
    }

    /** 设置页里的「截图标注」分区：只看不改，改键统一回快捷键总表。 */
    function ShotSettingsSection() {
      const [keys, setKeys] = useState(readKeys)
      useEffect(() => {
        if (!catalogStore || typeof catalogStore.subscribe !== 'function') return undefined
        return catalogStore.subscribe(() => setKeys(readKeys()))
      }, [])
      const rowStyle = { display: 'flex', alignItems: 'center', gap: 12, padding: '10px 0' }
      const keyStyle = {
        fontFamily: 'monospace', fontSize: 13, padding: '2px 8px', borderRadius: 4,
        background: 'var(--dsh-surface-raised, rgba(127,127,127,0.15))',
      }
      return h('div', { style: { padding: '4px 0' } },
        h('div', { style: rowStyle },
          h('span', null, '当前快捷键'),
          h('span', { style: keyStyle }, keys || '未设置'),
        ),
        h('div', { style: { ...rowStyle, color: 'var(--dsh-text-secondary, rgba(127,127,127,0.9))', fontSize: 13 } },
          '改键请到「通用设置 → 快捷键」里搜「截图标注」。改完这里的全局热键会自动跟着变。',
        ),
        h('div', { style: { ...rowStyle, color: 'var(--dsh-text-secondary, rgba(127,127,127,0.9))', fontSize: 13 } },
          '快捷键在任何窗口都能用；截完的图会自动贴进当前会话的输入框。',
        ),
      )
    }

    function ShotButton() {
      const [hover, setHover] = useState(false)
      return h('button', {
        type: 'button',
        title: '截图标注',
        'aria-label': '截图标注',
        onClick: () => void captureViaHost(),
        onMouseEnter: () => setHover(true),
        onMouseLeave: () => setHover(false),
        style: {
          display: 'grid', placeItems: 'center',
          width: 28, height: 28, padding: 0, border: 0, borderRadius: 6,
          background: hover ? 'var(--dsh-surface-raised, rgba(127,127,127,0.18))' : 'transparent',
          color: 'inherit', cursor: 'pointer',
        },
      }, h('svg', {
        viewBox: '0 0 24 24', width: 16, height: 16, fill: 'none',
        stroke: 'currentColor', strokeWidth: 1.8, strokeLinecap: 'round', strokeLinejoin: 'round',
        'aria-hidden': true,
      },
        h('path', { d: 'M4 8V6a2 2 0 0 1 2-2h2' }),
        h('path', { d: 'M16 4h2a2 2 0 0 1 2 2v2' }),
        h('path', { d: 'M20 16v2a2 2 0 0 1-2 2h-2' }),
        h('path', { d: 'M8 20H6a2 2 0 0 1-2-2v-2' }),
        h('circle', { cx: 12, cy: 12, r: 3 }),
      ))
    }

    return {
      inject: ['slots', 'shortcuts'],
      apply(ctx) {
        catalogStore = ctx.shortcuts && ctx.shortcuts.catalog ? ctx.shortcuts.catalog : null
        // Ctrl+Alt+S：与 ui-workspace 的 session.archive（Ctrl+Shift+A / 网页 Ctrl+Alt+A）不冲突。
        // 注意 web:linux 只允许 Ctrl+/ 等三个组合，这里不声明，避免注册即抛错。
        const onKey = ['desktop:macos', 'desktop:windows', 'desktop:linux', 'web:macos', 'web:windows']
        const defaults = Object.fromEntries(onKey.map((profile) => [profile, { code: 'KeyS', modifiers: ['primary', 'alt'] }]))
        ctx.effect(() => ctx.shortcuts.register({
          id: 'dsh-screenshot.capture',
          label: () => '截图标注',
          aliases: ['screenshot', 'snip', '截图'],
          defaults,
          regions: ['page', 'editable'],
          modals: [],
          // 真正的截屏由全局热键小程序执行（它读同一份 keybindings.json，所以这里
          // 改键会同步到全局键）。这里只负责「占位 + 吃掉按键」，避免同一个键
          // 被服务和小程序各触发一次。
          resolve: () => ({ status: 'handled', run: () => {} }),
        }), 'screenshot: shortcut')

        ctx.slots.inject('conversation.input.left', () => ctx.slots.register({
          name: 'conversation.input.left', id: 'dsh-shot-hotkey2', order: 0,
        }, ShotButton))

        // 设置页自己的分区（和 dsh-pet 一样），只展示不编辑。
        ctx.slots.inject('settings.section', () => ctx.slots.register({
          name: 'settings.section', id: 'dsh-shot-hotkey2', order: 40, label: () => '截图标注',
        }, ShotSettingsSection))

        ctx.effect(() => {
          const controller = new AbortController()
          void drainExternalCaptures(controller)
          return () => controller.abort()
        }, 'screenshot: external capture intake')
      },
    }
  },
})
