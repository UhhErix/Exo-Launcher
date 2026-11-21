import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { Close, Filter, Search } from '../../brand/icons'
import { useDismissOnAncestorScroll } from '../../lib/dismiss'
import { useGlide } from '../../lib/glide'
import { filterKey, filterLabel, type LibraryFilter } from '../../lib/librarySearch'
import { GiftsPill } from './GiftsShelf'

export type FilterOption = { section: string; filter: LibraryFilter; disabled?: boolean }
export type LibraryTab = 'home' | 'installed' | 'all' | 'updates'

export function LibraryHead({
  empty,
  tab,
  onTab,
  total,
  installedCount,
  updatesCount,
  found,
  query,
  onQuery,
  understood,
  filters,
  options,
  onFilter,
  onClear,
  onWarmFilters,
  tools,
}: {
  empty: boolean
  tab: LibraryTab
  onTab: (next: LibraryTab) => void
  total: number
  installedCount: number
  updatesCount: number
  found: readonly unknown[] | null
  query: string
  onQuery: (query: string) => void
  understood: string[]
  filters: LibraryFilter[]
  options: FilterOption[]
  onFilter: (filter: LibraryFilter) => void
  onClear: () => void
  onWarmFilters: () => void
  tools: ReactNode
}) {
  const field = useRef<HTMLInputElement>(null)
  const tabsGlide = useGlide<HTMLDivElement>()
  const isHome = tab === 'home'
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.defaultPrevented || !(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'f' || event.shiftKey || event.altKey) return
      const room = document.querySelector('[data-log-area="library"]')
      if (!room || room.hasAttribute('hidden') || room.classList.contains('is-backstage')) return
      event.preventDefault()
      if (isHome) {
        onTab('all')
        requestAnimationFrame(() => {
          field.current?.focus()
          field.current?.select()
        })
        return
      }
      field.current?.focus()
      field.current?.select()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [isHome, onTab])
  if (empty) return <h1 className="sr-only">Library</h1>
  const tabs: ReadonlyArray<{ id: LibraryTab; label: string; count?: number }> = [
    { id: 'home', label: 'Home' },
    { id: 'installed', label: 'Installed', count: installedCount },
    { id: 'all', label: 'All Games', count: total },
    { id: 'updates', label: 'Updates', count: updatesCount },
  ]
  return (
    <>
      <header className={`exo-library-head${isHome ? ' is-home' : ''}`}>
        <div className="exo-library-tabs-wrap">
          <h1 className="sr-only">Library</h1>
          <div ref={tabsGlide} className="exo-glass exo-shelf-chips exo-glide exo-library-tabs" role="group" aria-label="Library view">
            {tabs.map(item => (
              <button
                key={item.id}
                type="button"
                className="exo-shelf-chip"
                aria-pressed={tab === item.id}
                onClick={() => onTab(item.id)}
              >
                <span>{item.label}</span>
                {item.count != null ? (
                  <span className={`exo-library-tab-count tabular-nums${item.id === 'updates' && item.count > 0 ? ' has-updates' : ''}`}>
                    {item.count.toLocaleString('en-US')}
                  </span>
                ) : null}
              </button>
            ))}
          </div>
          {!isHome && found != null ? (
            <span className="exo-library-chip tabular-nums">
              {`${found.length.toLocaleString('en-US')} of ${total.toLocaleString('en-US')}`}
            </span>
          ) : null}
        </div>
        {!isHome ? (
          <label className="exo-library-search">
            <Search size={16} />
            <input
              ref={field}
              type="search"
              value={query}
              placeholder="Search your games"
              aria-label="Search your games"
              spellCheck={false}
              autoComplete="off"
              onChange={event => onQuery(event.target.value)}
              onKeyDown={event => {
                if (event.key !== 'Escape' || !query) return
                event.preventDefault()
                event.stopPropagation()
                onQuery('')
              }}
            />
            {query ? (
              <button type="button" className="exo-library-search-clear" aria-label="Clear search" onClick={() => { onQuery(''); field.current?.focus() }}>
                <Close size={14} />
              </button>
            ) : null}
          </label>
        ) : null}
        <div className="exo-library-tools">
          {isHome ? <GiftsPill /> : <FilterPopover filters={filters} options={options} onFilter={onFilter} onClear={onClear} onWarm={onWarmFilters} />}
          {tools}
        </div>
      </header>
      {!isHome && (filters.length || understood.length) ? (
        <div className="exo-library-chips" role="group" aria-label="Filters">
          {understood.map(word => <span key={'q:' + word} className="exo-library-chip is-understood"><Search size={12} />{word}</span>)}
          {filters.map(filter => (
            <button key={filterKey(filter)} type="button" className="exo-library-chip is-filter" aria-label={`Remove ${filterLabel(filter)}`} onClick={() => onFilter(filter)}>
              {filterLabel(filter)}<Close size={12} />
            </button>
          ))}
          {filters.length ? <button type="button" className="exo-set-btn is-compact exo-library-clear" onClick={onClear}>Clear</button> : null}
        </div>
      ) : null}
    </>
  )
}

/**
 * Filter: a popover of chips from its button (large glass, as a menu), one group per kind (Show, Store, Genre, Mode),
 * each chip a toggle (a white pill while on); it stays open while you pick, and Escape or a press outside closes it.
 * Chips in a group widen the grid (Steam or Epic), groups narrow it (and installed): librarySearch › matchesFilters.
 */
function FilterPopover({ filters, options, onFilter, onClear, onWarm }: {
  filters: LibraryFilter[]
  options: FilterOption[]
  onFilter: (filter: LibraryFilter) => void
  onClear: () => void
  onWarm: () => void
}) {
  const id = useId()
  const triggerRef = useRef<HTMLButtonElement>(null)
  const pop = useRef<HTMLDivElement>(null)
  const [open, setOpen] = useState(false)
  useDismissOnAncestorScroll(open, triggerRef, pop, () => pop.current?.hidePopover())
  const on = new Set(filters.map(filterKey))
  const sections = [...new Set(options.map(option => option.section))]
  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        className={`exo-shelf-sort-trigger exo-library-filter${filters.length ? ' is-on' : ''}`}
        style={{ anchorName: '--exo-library-filter' }}
        aria-label={filters.length ? `Filter, ${filters.length} on` : 'Filter'}
        aria-haspopup="dialog"
        popoverTarget={id}
        onPointerEnter={onWarm}
        onFocus={onWarm}
      >
        <Filter size={16} />Filter{filters.length ? <span className="exo-library-filter-count tabular-nums">{filters.length}</span> : null}
      </button>
      <div
        ref={pop}
        id={id}
        popover="auto"
        role="dialog"
        aria-label="Filter"
        className="exo-shelf-sort-menu exo-library-filter-pop"
        style={{ positionAnchor: '--exo-library-filter', marginTop: 4 }}
        onToggle={event => {
          const isOpen = event.newState === 'open'
          setOpen(isOpen)
          if (isOpen) pop.current?.querySelector<HTMLElement>('button')?.focus()
        }}
      >
        {sections.map(section => (
          <div key={section} className="exo-library-filter-group" role="group" aria-label={section}>
            <div className="exo-menu-section" role="presentation">{section}</div>
            <div className="exo-library-filter-chips">
              {options.filter(option => option.section === section).map(({ filter, disabled }) => (
                <button
                  key={filterKey(filter)}
                  type="button"
                  className="exo-shelf-chip"
                  aria-pressed={on.has(filterKey(filter))}
                  disabled={disabled}
                  onClick={() => onFilter(filter)}
                >
                  {filterLabel(filter)}
                </button>
              ))}
            </div>
          </div>
        ))}
        {filters.length ? <button type="button" className="exo-set-btn is-compact exo-library-filter-clear" onClick={onClear}>Clear all</button> : null}
      </div>
    </>
  )
}
