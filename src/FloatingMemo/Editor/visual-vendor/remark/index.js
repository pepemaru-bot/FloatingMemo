// Note: types exposed from `index.d.ts`
import remarkParse from '../remark-parse/index.js'
import remarkStringify from '../remark-stringify/index.js'
import {unified} from '../unified/index.js'

/**
 * Create a new unified processor that already uses `remark-parse` and
 * `remark-stringify`.
 */
export const remark = unified().use(remarkParse).use(remarkStringify).freeze()
