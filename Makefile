NODE ?= node

.DEFAULT_GOAL := help

.PHONY: help merge release

COMMON_RELEASE_ARGS = \
	$(if $(strip $(DRY_RUN)),--dry-run) \
	$(if $(filter 1 true yes on,$(PUSH)),--push) \
	$(if $(filter 0 false no off,$(PUSH)),--no-push) \
	$(if $(strip $(NO_PUSH)),--no-push) \
	$(if $(strip $(TARGET_BRANCH)),--target-branch "$(TARGET_BRANCH)") \
	$(if $(strip $(TAG_PREFIX)),--tag-prefix "$(TAG_PREFIX)") \
	$(if $(strip $(LATEST_TAG)),--latest-tag "$(LATEST_TAG)")

MERGE_ARGS = \
	$(COMMON_RELEASE_ARGS) \
	$(if $(strip $(UPSTREAM_REMOTE)),--upstream-remote "$(UPSTREAM_REMOTE)") \
	$(if $(strip $(UPSTREAM_URL)),--upstream-url "$(UPSTREAM_URL)") \
	$(if $(strip $(UPSTREAM_BRANCH)),--upstream-branch "$(UPSTREAM_BRANCH)")

merge:
	@$(NODE) tools/gameempire_merge_latest.js $(MERGE_ARGS)

release:
	@$(NODE) tools/gameempire_merge_latest.js --release-only $(COMMON_RELEASE_ARGS)

help:
	@echo "Unity MCP fork tools"
	@echo ""
	@echo "Targets:"
	@echo "  make merge          Fetch upstream beta, merge into gameempire/protect, create immutable tag, update latest tag"
	@echo "  make release        Release current gameempire/protect without merging upstream"
	@echo ""
	@echo "Options:"
	@echo "  DRY_RUN=1           Print git commands without changing the repo"
	@echo "  PUSH=1              Push target branch, immutable tag, and gameempire-mcp-latest (default)"
	@echo "  NO_PUSH=1           Do not push after the local branch/tag update"
	@echo "  UPSTREAM_BRANCH=x   Override upstream branch (default: beta)"
	@echo "  TARGET_BRANCH=x     Override target branch (default: gameempire/protect)"
