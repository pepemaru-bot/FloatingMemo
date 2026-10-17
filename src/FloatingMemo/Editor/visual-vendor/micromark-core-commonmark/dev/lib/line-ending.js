/**
 * @import {
 *   Construct,
 *   State,
 *   TokenizeContext,
 *   Tokenizer
 * } from 'micromark-util-types'
 */

import {ok as assert} from '../../../devlop/lib/default.js'
import {factorySpace} from '../../../micromark-factory-space/index.js'
import {markdownLineEnding} from '../../../micromark-util-character/index.js'
import {types} from '../../../micromark-util-symbol/lib/default.js'

/** @type {Construct} */
export const lineEnding = {name: 'lineEnding', tokenize: tokenizeLineEnding}

/**
 * @this {TokenizeContext}
 *   Context.
 * @type {Tokenizer}
 */
function tokenizeLineEnding(effects, ok) {
  return start

  /** @type {State} */
  function start(code) {
    assert(markdownLineEnding(code), 'expected eol')
    effects.enter(types.lineEnding)
    effects.consume(code)
    effects.exit(types.lineEnding)
    return factorySpace(effects, ok, types.linePrefix)
  }
}
