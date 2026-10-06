import { Fragment } from 'react'
import type { Coverage } from './types'
import { acquisitionPathLabel, collectionLabel, counterLabel, explainCoverage, inputLabel, safeCoverageErrors, safeDiagnostics, workIqAcquisitionCounters } from './coverage'

const statusLabels = { complete: '取得完了', partial: '一部取得', unavailable: '取得不可' }

export default function CoverageTable({ coverage, expandDetails = false }: { coverage: Coverage[]; expandDetails?: boolean }) {
  return (
    <section className="coverage-section" aria-label="取得範囲の診断">
      <p className="coverage-note">取得＝取得処理が返した件数、有効取得＝期間内の重複除外済み情報、採用＝最終生成入力。検索ヒット数と発言数は同じではありません。取得状態と入力削減は別です。未報告は「不明」とし、0件だけで不存在・アクセス拒否を判定しません。</p>
      <div className="table-scroll">
        <table className="coverage-table">
          <caption>取得範囲・情報源の状態</caption>
          <thead><tr><th>情報源</th><th>取得状態／入力</th><th>取得</th><th>有効取得</th><th>採用</th><th>ページ</th><th>解釈・診断</th></tr></thead>
          <tbody>{coverage.map((source, index) => {
            const explanations = explainCoverage(source)
            const diagnostics = safeDiagnostics(source.diagnostics)
            const errors = safeCoverageErrors(source.errors)
            const acquisition = workIqAcquisitionCounters(source)
            const acquisitionPath = acquisitionPathLabel(source.acquisitionPath)
            return <tr key={`${source.sourceType}-${index}`}>
              <th scope="row">{source.sourceType}</th>
              <td>
                {acquisitionPath ? <p>{acquisitionPath}</p> : null}
                {acquisition.length ? <dl className="coverage-counters" aria-label={`${source.sourceType}の取得経路と除外`}>
                  {acquisition.map((item) => <Fragment key={item.label}><dt>{item.label}</dt><dd>{counterLabel(item.count)}</dd></Fragment>)}
                </dl> : null}
                {acquisition.length ? <p>承認予約数は成功件数ではなく、結果未確定の要求も含みます。</p> : null}
                <div>{source.collectionStatus ? collectionLabel(source.collectionStatus) : `取得状態：不明（旧総合状態：${statusLabels[source.status] ?? source.status}）`}</div>
                <div>{inputLabel(source.inputStatus)}</div>
              </td>
              <td>{counterLabel(source.fetchedCount)}</td><td>{counterLabel(source.retrievedCount)}</td>
              <td>{counterLabel(source.count)}</td><td>{counterLabel(source.pages)}</td>
              <td>
                {explanations.length ? <ul className="coverage-explanations">{explanations.map((item, itemIndex) =>
                  <li key={itemIndex}><strong>{item.category}</strong>：{item.description}</li>,
                )}</ul> : <span>探索した範囲の取得が完了しています。</span>}
                {source.reason || Object.keys(diagnostics).length || errors.length || source.sentCount != null || source.truncatedCount != null || source.inputDroppedCount != null ? <details className="coverage-details" open={expandDetails}><summary>技術詳細（元の理由・取得段階）</summary>
                  {source.reason ? <pre>{source.reason}</pre> : null}
                  <dl className="coverage-counters">
                    <dt>最終送信件数</dt><dd>{counterLabel(source.sentCount)}</dd>
                    <dt>本文短縮件数</dt><dd>{counterLabel(source.truncatedCount)}</dd>
                    <dt>入力から除外した件数</dt><dd>{counterLabel(source.inputDroppedCount)}</dd>
                  </dl>
                  {errors.length ? <ul>{errors.map((error, errorIndex) =>
                    <li key={errorIndex}>{error.stage}：HTTP {error.httpStatus} / {error.graphCode ?? 'コード未報告'} / {error.count} 回</li>,
                  )}</ul> : null}
                  {Object.keys(diagnostics).length ? <pre>{JSON.stringify(diagnostics, null, 2)}</pre> : null}
                </details> : null}
              </td>
            </tr>
          })}</tbody>
        </table>
      </div>
    </section>
  )
}
