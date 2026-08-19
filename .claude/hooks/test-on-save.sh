#!/bin/bash
# Auto-run tests when test files change
if [[ "$1" == *test* ]] || [[ "$1" == *spec* ]]; then
  npm test --watchAll=false 2>&1 | head -20
fi