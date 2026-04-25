NODE ?= node

.DEFAULT_GOAL := help

.PHONY: help merge

merge:
	@$(NODE) tools/gameempire_merge_latest.js \
		$(if $(strip $(DRY_RUN)),--dry-run) \
		$(if $(strip $(PUSH)),--push) \
		$(if $(strip $(UPSTREAM_REMOTE)),--upstream-remote "$(UPSTREAM_REMOTE)") \
		$(if $(strip $(UPSTREAM_URL)),--upstream-url "$(UPSTREAM_URL)") \
		$(if $(strip $(UPSTREAM_BRANCH)),--upstream-branch "$(UPSTREAM_BRANCH)") \
		$(if $(strip $(TARGET_BRANCH)),--target-branch "$(TARGET_BRANCH)") \
		$(if $(strip $(TAG_PREFIX)),--tag-prefix "$(TAG_PREFIX)") \
		$(if $(strip $(LATEST_TAG)),--latest-tag "$(LATEST_TAG)")

help:
	@echo "Unity MCP fork tools"
	@echo ""
	@echo "Targets:"
	@echo "  make merge          Fetch upstream beta, merge into gameempire/protect, create immutable tag, update latest tag"
	@echo ""
	@echo "Options:"
	@echo "  DRY_RUN=1           Print git commands without changing the repo"
	@echo "  PUSH=1              Push target branch, immutable tag, and gameempire-mcp-latest"
	@echo "  UPSTREAM_BRANCH=x   Override upstream branch (default: beta)"
	@echo "  TARGET_BRANCH=x     Override target branch (default: gameempire/protect)"
