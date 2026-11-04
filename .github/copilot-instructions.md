# Change delivery

- Create a fresh feature or fix branch for each new change; do not reuse a merged branch or work directly on `main`.
- Commit completed changes after the relevant validation.
- When work is ready, push the branch, create a pull request targeting `main`, and merge it after required checks pass. Then delete the merged branch remotely and locally.
- Do not push changes directly to `main`, bypass required checks or protections, or delete a branch with unmerged work. Report blockers rather than forcing a merge.

- @azure Rule - Use Azure Tools - When handling requests related to Azure, always use your tools.
- @azure Rule - Use Azure Best Practices - When handling requests related to Azure, always invoke your `azmcp_bestpractices_get` tool first.
- @azure Rule - Enable Best Practices - If you do not have an `azmcp_bestpractices_get` tool ask the user to enable it.
