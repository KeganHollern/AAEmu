-- Read only. Run against the selected Game database. No names, text, or item details leave SQL.
WITH mail_exposure AS (
  SELECT m.*,
    (money_amount_1 <> 0 OR money_amount_2 <> 0 OR money_amount_3 <> 0 OR
     attachment0 <> 0 OR attachment1 <> 0 OR attachment2 <> 0 OR attachment3 <> 0 OR
     attachment4 <> 0 OR attachment5 <> 0 OR attachment6 <> 0 OR attachment7 <> 0 OR
     attachment8 <> 0 OR attachment9 <> 0) AS has_contents,
    (type IN (1, 2) AND returned = 0 AND sender_id <> receiver_id AND sender_id <> 0
     AND EXISTS (SELECT 1 FROM characters c WHERE c.id=m.sender_id AND c.deleted=0)) AS can_return
  FROM mails m
  WHERE received_date <= UTC_TIMESTAMP() - INTERVAL 14 DAY
)
SELECT type, status, returned, has_contents, can_return,
  COUNT(*) AS expired_mail_count,
  SUM(money_amount_1) AS copper_total,
  SUM(money_amount_2) AS billing_amount_total,
  SUM(money_amount_3) AS alternate_amount_total,
  SUM((attachment0 <> 0) + (attachment1 <> 0) + (attachment2 <> 0) + (attachment3 <> 0) +
      (attachment4 <> 0) + (attachment5 <> 0) + (attachment6 <> 0) + (attachment7 <> 0) +
      (attachment8 <> 0) + (attachment9 <> 0)) AS item_references
FROM mail_exposure
GROUP BY type, status, returned, has_contents, can_return
ORDER BY type, status, returned, has_contents, can_return;
